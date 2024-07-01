using System;
using System.Runtime.InteropServices;
using VRCFaceTracking.Core.Types;

namespace VirtualDesktop.FaceTracking
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Quaternion
    {
        #region Static Fields
        public static readonly Quaternion Identity = new Quaternion(0.0f, 0.0f, 0.0f, 1.0f);
        #endregion

        #region Fields
        public float X;
        public float Y;
        public float Z;
        public float W;
        #endregion

        #region Constructor
        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
        #endregion

        #region Methods
        public Vector2 Cartesian()
        {
            float magnitude = (float)Math.Sqrt(X*X + Y*Y + Z*Z + W*W);
            float Xm = X / magnitude;
            float Ym = Y / magnitude;
            float Zm = Z / magnitude;
            float Wm = W / magnitude;

            float pitch = (float)Math.Asin(2 * (Xm*Zm - Wm*Ym));
            float yaw = (float)Math.Atan2(2 * (Ym*Zm + Wm*Xm), Wm*Wm - Xm*Xm - Ym*Ym + Zm*Zm);

            return new Vector2(pitch, yaw);
        }
        #endregion
    }
}