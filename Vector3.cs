using System.Runtime.InteropServices;

namespace VirtualDesktop.FaceTracking
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3
    {
        #region Fields
        public float X;
        public float Y;
        public float Z;
        #endregion
    }
}