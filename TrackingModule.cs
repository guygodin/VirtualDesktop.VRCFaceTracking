using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;

namespace VirtualDesktop.FaceTracking
{
    public unsafe class TrackingModule : ExtTrackingModule
    {
        #region Constants
        private const string FaceStateMapName = "VirtualDesktop.FaceState";
        private const string FaceStateEventName = "VirtualDesktop.FaceStateEvent";
        #endregion

        #region Fields
        private MemoryMappedFile _mappedFile;
        private MemoryMappedViewAccessor _mappedView;
        private FaceState* _faceState;
        private EventWaitHandle _faceStateEvent;
        private bool? _isTracking = null;
        #endregion

        #region Properties
        private bool? IsTracking
        {
            get { return _isTracking; }
            set
            {
                if (value != _isTracking)
                {
                    _isTracking = value;
                    if ((bool)value)
                    {
                        Logger.LogInformation("[VirtualDesktop] Tracking is now active!");
                    }
                    else
                    {
                        Logger.LogWarning("[VirtualDesktop] Tracking is not active. Make sure you are connected to your computer, a VR game or SteamVR is launched and face/eye tracking is enabled in the Streaming tab.");
                    }
                }
            }
        }
        #endregion

        #region Overrides
        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "Virtual Desktop";

            var stream = GetType().Assembly.GetManifestResourceStream("VirtualDesktop.FaceTracking.Resources.Logo256.png");
            if (stream != null)
            {
                ModuleInformation.StaticImages = new List<Stream>() 
                { 
                    stream
                };
            }

            try
            {
                var size = Marshal.SizeOf<FaceState>();
                _mappedFile = MemoryMappedFile.OpenExisting(FaceStateMapName, MemoryMappedFileRights.ReadWrite);
                _mappedView = _mappedFile.CreateViewAccessor(0, size);

                byte* ptr = null;
                _mappedView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _faceState = (FaceState*)ptr;

                _faceStateEvent = EventWaitHandle.OpenExisting(FaceStateEventName);
            }
            catch
            {
                Logger.LogError("[VirtualDesktop] Failed to open MemoryMappedFile. Make sure the Virtual Desktop Streamer (v1.29 or later) is running.");
                return (false, false);
            }

            return (true, true);
        }

        public override void Update()
        {
            if (Status == ModuleState.Active)
            {
                if (_faceStateEvent.WaitOne(50))
                {
                    UpdateTracking();
                }
                else
                {
                    var faceState = _faceState;
                    IsTracking = faceState != null && (faceState->LeftEyeIsValid || faceState->RightEyeIsValid || faceState->IsEyeFollowingBlendshapesValid || faceState->FaceIsValid);
                }
            }
            else
            {
                Thread.Sleep(10);
            }
        }

        public override void Teardown()
        {
            if (_faceState != null)
            {
                _faceState = null;
                if (_mappedView != null)
                {
                    _mappedView.Dispose();
                    _mappedView = null;
                }
                if (_mappedFile != null)
                {
                    _mappedFile.Dispose();
                    _mappedFile = null;
                }
            }
            if (_faceStateEvent != null)
            {
                _faceStateEvent.Dispose();
                _faceStateEvent = null;
            }
            _isTracking = null;
        }
        #endregion

        #region Methods
        /// <summary>
        /// Credit https://github.com/regzo2/VRCFaceTracking-QuestProOpenXR for calculations on converting from OpenXR weigths to VRCFT shapes
        /// </summary>
        private void UpdateTracking()
        {
            var isTracking = false;

            var faceState = _faceState;
            if (faceState != null)
            {
                var expressions = faceState->ExpressionWeights;

                if (faceState->LeftEyeIsValid || faceState->RightEyeIsValid)
                {                    
                    var leftEyePose = faceState->LeftEyePose;
                    var rightEyePose = faceState->RightEyePose;
                    UpdateEyeData(UnifiedTracking.Data.Eye, expressions, leftEyePose.Orientation, rightEyePose.Orientation);
                    isTracking = true;
                }

                if (faceState->IsEyeFollowingBlendshapesValid)
                {
                    UpdateEyeExpressions(UnifiedTracking.Data.Shapes, expressions);
                    isTracking = true;
                }

                if (faceState->FaceIsValid)
                {
                    UpdateMouthExpressions(UnifiedTracking.Data.Shapes, expressions);
                    isTracking = true;
                }
            }

            IsTracking = isTracking;
        }

        private void UpdateEyeData(UnifiedEyeData eye, float* expressions, Quaternion orientationL, Quaternion orientationR)
        {
            // Eye Openness parsing
            eye.Left.Openness = 1.0f - Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedL] + expressions[(int)Expressions.EyesClosedL] * expressions[(int)Expressions.LidTightenerL]));
            eye.Right.Openness = 1.0f - (float)Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedR] + expressions[(int)Expressions.EyesClosedR] * expressions[(int)Expressions.LidTightenerR]));

            // Eye Gaze parsing
            double qx = orientationL.X;
            double qy = orientationL.Y;
            double qz = orientationL.Z;
            double qw = orientationL.W;

            var yaw = Math.Atan2(2.0 * (qy * qz + qw * qx), qw * qw - qx * qx - qy * qy + qz * qz);
            var pitch = Math.Asin(-2.0 * (qx * qz - qw * qy));

            var pitchL = (180.0 / Math.PI) * pitch; // from radians
            var yawL = (180.0 / Math.PI) * yaw;

            qx = orientationL.X;
            qy = orientationL.Y;
            qz = orientationL.Z;
            qw = orientationL.W;
            yaw = Math.Atan2(2.0 * (qy * qz + qw * qx), qw * qw - qx * qx - qy * qy + qz * qz);
            pitch = Math.Asin(-2.0 * (qx * qz - qw * qy));

            var pitchR = (180.0 / Math.PI) * pitch; // from radians
            var yawR = (180.0 / Math.PI) * yaw;

            // Eye Data to UnifiedEye
            var radianConst = 0.0174533f;

            var pitchRmod = (float)(Math.Abs(pitchR) + 4f * Math.Pow(Math.Abs(pitchR) / 30f, 30f)); // curves the tail end to better accomodate actual eye pos.
            var pitchLmod = (float)(Math.Abs(pitchL) + 4f * Math.Pow(Math.Abs(pitchL) / 30f, 30f));
            var yawRmod = (float)(Math.Abs(yawR) + 6f * Math.Pow(Math.Abs(yawR) / 27f, 18f)); // curves the tail end to better accomodate actual eye pos.
            var yawLmod = (float)(Math.Abs(yawL) + 6f * Math.Pow(Math.Abs(yawL) / 27f, 18f));

            eye.Right.Gaze = new Vector2(pitchR < 0 ? pitchRmod * radianConst : -1 * pitchRmod * radianConst, yawR < 0 ? -1 * yawRmod * radianConst : (float)yawR * radianConst);
            eye.Left.Gaze = new Vector2(pitchL < 0 ? pitchLmod * radianConst : -1 * pitchLmod * radianConst, yawL < 0 ? -1 * yawLmod * radianConst : (float)yawL * radianConst);

            // Eye dilation code, automated process maybe?
            eye.Left.PupilDiameter_MM = 5f;
            eye.Right.PupilDiameter_MM = 5f;

            // Force the normalization values of Dilation to fit avg. pupil values.
            eye._minDilation = 0;
            eye._maxDilation = 10;
        }

        private void UpdateEyeExpressions(UnifiedExpressionShape[] unifiedExpressions, float* expressions)
        {
            // Eye Expressions Set
            unifiedExpressions[(int)UnifiedExpressions.EyeWideLeft].Weight = expressions[(int)Expressions.UpperLidRaiserL];
            unifiedExpressions[(int)UnifiedExpressions.EyeWideRight].Weight = expressions[(int)Expressions.UpperLidRaiserR];

            unifiedExpressions[(int)UnifiedExpressions.EyeSquintLeft].Weight = expressions[(int)Expressions.LidTightenerL];
            unifiedExpressions[(int)UnifiedExpressions.EyeSquintRight].Weight = expressions[(int)Expressions.LidTightenerR];

            // Brow Expressions Set
            unifiedExpressions[(int)UnifiedExpressions.BrowInnerUpLeft].Weight = expressions[(int)Expressions.InnerBrowRaiserL];
            unifiedExpressions[(int)UnifiedExpressions.BrowInnerUpRight].Weight = expressions[(int)Expressions.InnerBrowRaiserR];
            unifiedExpressions[(int)UnifiedExpressions.BrowOuterUpLeft].Weight = expressions[(int)Expressions.OuterBrowRaiserL];
            unifiedExpressions[(int)UnifiedExpressions.BrowOuterUpRight].Weight = expressions[(int)Expressions.OuterBrowRaiserR];

            unifiedExpressions[(int)UnifiedExpressions.BrowPinchLeft].Weight = expressions[(int)Expressions.BrowLowererL];
            unifiedExpressions[(int)UnifiedExpressions.BrowLowererLeft].Weight = expressions[(int)Expressions.BrowLowererL];
            unifiedExpressions[(int)UnifiedExpressions.BrowPinchRight].Weight = expressions[(int)Expressions.BrowLowererR];
            unifiedExpressions[(int)UnifiedExpressions.BrowLowererRight].Weight = expressions[(int)Expressions.BrowLowererR];
        }

        private void UpdateMouthExpressions(UnifiedExpressionShape[] unifiedExpressions, float* expressions)
        {
            // Jaw Expression Set                        
            unifiedExpressions[(int)UnifiedExpressions.JawOpen].Weight = expressions[(int)Expressions.JawDrop];
            unifiedExpressions[(int)UnifiedExpressions.JawLeft].Weight = expressions[(int)Expressions.JawSidewaysLeft];
            unifiedExpressions[(int)UnifiedExpressions.JawRight].Weight = expressions[(int)Expressions.JawSidewaysRight];
            unifiedExpressions[(int)UnifiedExpressions.JawForward].Weight = expressions[(int)Expressions.JawThrust];

            // Mouth Expression Set   
            unifiedExpressions[(int)UnifiedExpressions.MouthClosed].Weight = expressions[(int)Expressions.LipsToward];

            unifiedExpressions[(int)UnifiedExpressions.MouthUpperLeft].Weight = expressions[(int)Expressions.MouthLeft];
            unifiedExpressions[(int)UnifiedExpressions.MouthLowerLeft].Weight = expressions[(int)Expressions.MouthLeft];
            unifiedExpressions[(int)UnifiedExpressions.MouthUpperRight].Weight = expressions[(int)Expressions.MouthRight];
            unifiedExpressions[(int)UnifiedExpressions.MouthLowerRight].Weight = expressions[(int)Expressions.MouthRight];

            unifiedExpressions[(int)UnifiedExpressions.MouthCornerPullLeft].Weight = expressions[(int)Expressions.LipCornerPullerL];
            unifiedExpressions[(int)UnifiedExpressions.MouthCornerSlantLeft].Weight = expressions[(int)Expressions.LipCornerPullerL]; // Slant (Sharp Corner Raiser) is baked into Corner Puller.
            unifiedExpressions[(int)UnifiedExpressions.MouthCornerPullRight].Weight = expressions[(int)Expressions.LipCornerPullerR];
            unifiedExpressions[(int)UnifiedExpressions.MouthCornerSlantRight].Weight = expressions[(int)Expressions.LipCornerPullerR]; // Slant (Sharp Corner Raiser) is baked into Corner Puller.
            unifiedExpressions[(int)UnifiedExpressions.MouthFrownLeft].Weight = expressions[(int)Expressions.LipCornerDepressorL];
            unifiedExpressions[(int)UnifiedExpressions.MouthFrownRight].Weight = expressions[(int)Expressions.LipCornerDepressorR];

            unifiedExpressions[(int)UnifiedExpressions.MouthLowerDownLeft].Weight = expressions[(int)Expressions.LowerLipDepressorL];
            unifiedExpressions[(int)UnifiedExpressions.MouthLowerDownRight].Weight = expressions[(int)Expressions.LowerLipDepressorR];

            unifiedExpressions[(int)UnifiedExpressions.MouthUpperUpLeft].Weight = Math.Max(0, expressions[(int)Expressions.UpperLipRaiserL] - expressions[(int)Expressions.NoseWrinklerL]); // Workaround for upper lip up wierd tracking quirk.
            unifiedExpressions[(int)UnifiedExpressions.MouthUpperDeepenLeft].Weight = Math.Max(0, expressions[(int)Expressions.UpperLipRaiserL] - expressions[(int)Expressions.NoseWrinklerL]); // Workaround for upper lip up wierd tracking quirk.
            unifiedExpressions[(int)UnifiedExpressions.MouthUpperUpRight].Weight = Math.Max(0, expressions[(int)Expressions.UpperLipRaiserR] - expressions[(int)Expressions.NoseWrinklerR]); // Workaround for upper lip up wierd tracking quirk.
            unifiedExpressions[(int)UnifiedExpressions.MouthUpperDeepenRight].Weight = Math.Max(0, expressions[(int)Expressions.UpperLipRaiserR] - expressions[(int)Expressions.NoseWrinklerR]); // Workaround for upper lip up wierd tracking quirk.

            unifiedExpressions[(int)UnifiedExpressions.MouthRaiserUpper].Weight = expressions[(int)Expressions.ChinRaiserT];
            unifiedExpressions[(int)UnifiedExpressions.MouthRaiserLower].Weight = expressions[(int)Expressions.ChinRaiserB];

            unifiedExpressions[(int)UnifiedExpressions.MouthDimpleLeft].Weight = expressions[(int)Expressions.DimplerL];
            unifiedExpressions[(int)UnifiedExpressions.MouthDimpleRight].Weight = expressions[(int)Expressions.DimplerR];

            unifiedExpressions[(int)UnifiedExpressions.MouthTightenerLeft].Weight = expressions[(int)Expressions.LipTightenerL];
            unifiedExpressions[(int)UnifiedExpressions.MouthTightenerRight].Weight = expressions[(int)Expressions.LipTightenerR];

            unifiedExpressions[(int)UnifiedExpressions.MouthPressLeft].Weight = expressions[(int)Expressions.LipPressorL];
            unifiedExpressions[(int)UnifiedExpressions.MouthPressRight].Weight = expressions[(int)Expressions.LipPressorR];

            unifiedExpressions[(int)UnifiedExpressions.MouthStretchLeft].Weight = expressions[(int)Expressions.LipStretcherL];
            unifiedExpressions[(int)UnifiedExpressions.MouthStretchRight].Weight = expressions[(int)Expressions.LipStretcherR];

            // Lip Expression Set   
            unifiedExpressions[(int)UnifiedExpressions.LipPuckerUpperRight].Weight = expressions[(int)Expressions.LipPuckerR];
            unifiedExpressions[(int)UnifiedExpressions.LipPuckerLowerRight].Weight = expressions[(int)Expressions.LipPuckerR];
            unifiedExpressions[(int)UnifiedExpressions.LipPuckerUpperLeft].Weight = expressions[(int)Expressions.LipPuckerL];
            unifiedExpressions[(int)UnifiedExpressions.LipPuckerLowerLeft].Weight = expressions[(int)Expressions.LipPuckerL];

            unifiedExpressions[(int)UnifiedExpressions.LipFunnelUpperLeft].Weight = expressions[(int)Expressions.LipFunnelerLt];
            unifiedExpressions[(int)UnifiedExpressions.LipFunnelUpperRight].Weight = expressions[(int)Expressions.LipFunnelerRt];
            unifiedExpressions[(int)UnifiedExpressions.LipFunnelLowerLeft].Weight = expressions[(int)Expressions.LipFunnelerLb];
            unifiedExpressions[(int)UnifiedExpressions.LipFunnelLowerRight].Weight = expressions[(int)Expressions.LipFunnelerRb];

            unifiedExpressions[(int)UnifiedExpressions.LipSuckUpperLeft].Weight = Math.Min(1f - (float)Math.Pow(expressions[(int)Expressions.UpperLipRaiserL], 1f / 6f), expressions[(int)Expressions.LipSuckLt]);
            unifiedExpressions[(int)UnifiedExpressions.LipSuckUpperRight].Weight = Math.Min(1f - (float)Math.Pow(expressions[(int)Expressions.UpperLipRaiserR], 1f / 6f), expressions[(int)Expressions.LipSuckRt]);
            unifiedExpressions[(int)UnifiedExpressions.LipSuckLowerLeft].Weight = expressions[(int)Expressions.LipSuckLb];
            unifiedExpressions[(int)UnifiedExpressions.LipSuckLowerRight].Weight = expressions[(int)Expressions.LipSuckRb];

            // Cheek Expression Set   
            unifiedExpressions[(int)UnifiedExpressions.CheekPuffLeft].Weight = expressions[(int)Expressions.CheekPuffL];
            unifiedExpressions[(int)UnifiedExpressions.CheekPuffRight].Weight = expressions[(int)Expressions.CheekPuffR];
            unifiedExpressions[(int)UnifiedExpressions.CheekSuckLeft].Weight = expressions[(int)Expressions.CheekSuckL];
            unifiedExpressions[(int)UnifiedExpressions.CheekSuckRight].Weight = expressions[(int)Expressions.CheekSuckR];
            unifiedExpressions[(int)UnifiedExpressions.CheekSquintLeft].Weight = expressions[(int)Expressions.CheekRaiserL];
            unifiedExpressions[(int)UnifiedExpressions.CheekSquintRight].Weight = expressions[(int)Expressions.CheekRaiserR];

            // Nose Expression Set             
            unifiedExpressions[(int)UnifiedExpressions.NoseSneerLeft].Weight = expressions[(int)Expressions.NoseWrinklerL];
            unifiedExpressions[(int)UnifiedExpressions.NoseSneerRight].Weight = expressions[(int)Expressions.NoseWrinklerR];

            // Tongue Expression Set   
            // Future placeholder
            unifiedExpressions[(int)UnifiedExpressions.TongueOut].Weight = 0f;
        }
        #endregion
    }
}