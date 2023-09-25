using System.Runtime.InteropServices;

namespace VirtualDesktop.FaceTracking
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Pose
    {
        #region Static Fields
        public static readonly Pose Identity = new Pose { Orientation = Quaternion.Identity };
        #endregion

        #region Fields
        public Quaternion Orientation;
        public Vector3 Position;
        #endregion
    }
}