using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq.Expressions;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;
using Vector2 = VRCFaceTracking.Core.Types.Vector2;

namespace VirtualDesktop.FaceTracking
{
    public unsafe class TrackingModule : ExtTrackingModule
    {
        #region Constants
        private const string BodyStateMapName = "VirtualDesktop.BodyState";
        private const string BodyStateEventName = "VirtualDesktop.BodyStateEvent";
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
                        Logger.LogWarning("[VirtualDesktop] Tracking is not active. Make sure you are connected to your computer, a VR game or SteamVR is launched and 'Forward tracking data' is enabled in the Streaming tab.");
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
                ModuleInformation.StaticImages = new List<Stream> { stream };
            }

            try
            {
                var size = Marshal.SizeOf<FaceState>();
                _mappedFile = MemoryMappedFile.OpenExisting(BodyStateMapName, MemoryMappedFileRights.ReadWrite);
                _mappedView = _mappedFile.CreateViewAccessor(0, size);

                byte* ptr = null;
                _mappedView.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _faceState = (FaceState*)ptr;

                _faceStateEvent = EventWaitHandle.OpenExisting(BodyStateEventName);
            }
            catch
            {
                Logger.LogError("[VirtualDesktop] Failed to open MemoryMappedFile. Make sure the Virtual Desktop Streamer (v1.30 or later) is running.");
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
            #region Eye Openness parsing

            eye.Left.Openness = 
                1.0f - (float)Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedL]
                + expressions[(int)Expressions.CheekRaiserL] * expressions[(int)Expressions.LidTightenerL]));
            eye.Right.Openness =
                1.0f - (float)Math.Max(0, Math.Min(1, expressions[(int)Expressions.EyesClosedR]
                + expressions[(int)Expressions.CheekRaiserR] * expressions[(int)Expressions.LidTightenerR]));

            #endregion

            #region Eye Data to UnifiedEye

            eye.Right.Gaze = orientationR.Cartesian();
            eye.Left.Gaze = orientationL.Cartesian();

            // Eye dilation code, automated process maybe?
            eye.Left.PupilDiameter_MM = 5f;
            eye.Right.PupilDiameter_MM = 5f;

            // Force the normalization values of Dilation to fit avg. pupil values.
            eye._minDilation = 0;
            eye._maxDilation = 10;

            #endregion
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
            unifiedExpressions[(int)UnifiedExpressions.TongueOut].Weight = expressions[(int)Expressions.TongueOut];
            unifiedExpressions[(int)UnifiedExpressions.TongueCurlUp].Weight = expressions[(int)Expressions.TongueTipAlveolar];
        }
        #endregion
    }
}