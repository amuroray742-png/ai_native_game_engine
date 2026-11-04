using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiNative.Physics.Jolt
{
    internal static class JoltNative
    {
        private const string Library = "ainative_jolt";
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Definition
        {
            internal int Shape, Dynamic;
            internal Vector3 Size, Position;
            internal Quaternion Rotation;
            internal float Mass, Friction, Restitution;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BodyView
        {
            internal uint BodyId;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 Velocity, AngularVelocity;
            internal int Active, Dynamic;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Contact
        {
            internal uint First, Second;
            internal uint FirstShape, SecondShape;
            internal int Kind;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr aj_version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr aj_error();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern uint aj_world_count();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong aj_worker_contact_count();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_create(int workerThreads, out WorldHandle result);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void aj_destroy(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_add(WorldHandle handle, uint bodyId, in Definition definition);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_remove(WorldHandle handle, uint bodyId);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_impulse(WorldHandle handle, uint bodyId, Vector3 impulse);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_step(WorldHandle handle, float seconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_contacts(WorldHandle handle, [Out] Contact[]? items, int capacity, out int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_bodies(WorldHandle handle, [Out] BodyView[]? items, int capacity, out int count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_capture_state(WorldHandle handle, [Out] byte[]? bytes, int capacity, out int length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int aj_restore_state(WorldHandle handle, byte[] bytes, int length);

        internal static void Check(int status)
        {
            if (status != 0)
            {
                throw new InvalidOperationException(Marshal.PtrToStringAnsi(aj_error()) ?? "Native physics failure");
            }
        }
    }

    internal sealed class WorldHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WorldHandle() : base(true) { }
        protected override bool ReleaseHandle()
        {
            JoltNative.aj_destroy(handle);
            return true;
        }
    }
}
