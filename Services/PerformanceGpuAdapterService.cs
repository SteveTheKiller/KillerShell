using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillerShell.Services
{
    internal static class PerformanceGpuAdapterService
    {
        // DXGI returns adapter names and the exact locally unique IDs in GPU counters.
        // Enumeration is read-only and runs on the existing hardware discovery worker.
        internal static IReadOnlyDictionary<string, string> ReadAdapterNames(CancellationToken cancellationToken)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr factory = IntPtr.Zero;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                if (CreateDXGIFactory1(ref factoryId, out factory) < 0 || factory == IntPtr.Zero) return names;
                var enumerate = Method<EnumAdapters1>(factory, 12);
                for (uint index = 0; index < 64; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr adapter = IntPtr.Zero;
                    try
                    {
                        if (enumerate(factory, index, out adapter) < 0 || adapter == IntPtr.Zero) break;
                        if (Method<GetDesc1>(adapter, 10)(adapter, out AdapterDescription description) < 0) continue;
                        string name = (description.Description ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        string luid = "0x" + unchecked((uint)description.LuidHigh).ToString("x8", CultureInfo.InvariantCulture)
                            + "_0x" + description.LuidLow.ToString("x8", CultureInfo.InvariantCulture);
                        names[luid] = name;
                    }
                    finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // WMI names and explicitly unassigned counter cards remain available on failure.
            }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
            return names;
        }

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        [DllImport("dxgi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int CreateDXGIFactory1(ref Guid interfaceId, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1(IntPtr adapter, out AdapterDescription description);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Description;
            internal uint VendorId, DeviceId, SubsystemId, Revision;
            internal UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            internal uint LuidLow;
            internal int LuidHigh;
            internal uint Flags;
        }
    }
}
