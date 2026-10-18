using System.Runtime.InteropServices;

namespace Pitwall.Infrastructure.Windows;

public interface IMemorySource
{
    bool TryGetInstalledKilobytes(out ulong kilobytes, out int win32Error);
    bool TryGetAvailableBytes(out ulong bytes, out int win32Error);
}

public interface IPowerSource
{
    bool TryGetStatus(out int acLineStatus, out int batteryPercent, out int win32Error);
}

public sealed class KernelMemorySource : IMemorySource
{
    public static IMemorySource Default { get; } = new KernelMemorySource();

    public bool TryGetInstalledKilobytes(out ulong kilobytes, out int win32Error)
    {
        if (NativeMethods.GetPhysicallyInstalledSystemMemory(out kilobytes))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    public bool TryGetAvailableBytes(out ulong bytes, out int win32Error)
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            bytes = status.AvailablePhysicalBytes;
            win32Error = 0;
            return true;
        }

        bytes = 0;
        win32Error = Marshal.GetLastWin32Error();
        return false;
    }
}

public sealed class KernelPowerSource : IPowerSource
{
    public static IPowerSource Default { get; } = new KernelPowerSource();

    public bool TryGetStatus(out int acLineStatus, out int batteryPercent, out int win32Error)
    {
        if (NativeMethods.GetSystemPowerStatus(out var status))
        {
            acLineStatus = status.AcLineStatus;
            batteryPercent = status.BatteryLifePercent;
            win32Error = 0;
            return true;
        }

        acLineStatus = 255;
        batteryPercent = 255;
        win32Error = Marshal.GetLastWin32Error();
        return false;
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
