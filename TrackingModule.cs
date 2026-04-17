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
        private bool _eyeAvailable, _expressionAvailable;
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
        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, false); // Eye only

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

            (_eyeAvailable, _expressionAvailable) = (eyeAvailable, false); // Never initialize expressions
            return (_eyeAvailable, false); // Always return false for expression support
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
                    IsTracking = faceState != null && (faceState->LeftEyeIsValid || faceState->RightEyeIsValid || faceState->IsEyeFollowingBlendshapesValid);
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

                if (_eyeAvailable && faceState->LeftEyeIsValid || faceState->RightEyeIsValid)
                {
                    var leftEyePose = faceState->LeftEyePose;
                    var rightEyePose = faceState->RightEyePose;
                    UpdateEyeData(UnifiedTracking.Data.Eye, expressions, leftEyePose.Orientation, rightEyePose.Orientation);
                    isTracking = true;
                }

                if (_eyeAvailable && faceState->IsEyeFollowingBlendshapesValid)
                {
                    UpdateEyeExpressions(UnifiedTracking.Data.Shapes, expressions);
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
        #endregion
    }
}