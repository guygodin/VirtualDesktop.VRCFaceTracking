using System.Runtime.InteropServices;

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
    }
}