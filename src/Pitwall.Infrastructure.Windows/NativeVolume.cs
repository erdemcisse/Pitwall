using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pitwall.Infrastructure.Windows;

public sealed record VolumeLayout(
    string Root,
    string Label,
    string FileSystem,
    ulong CapacityBytes,
    ulong FreeBytes);

public sealed record VolumeDiskMapping(string Root, IReadOnlyList<int> DiskNumbers, int ExtentCount);

public interface IVolumeSource
{
    IReadOnlyList<string> GetDriveRoots();
    bool TryGetVolumeInfo(string root, out VolumeLayout layout, out int win32Error);
    bool TryGetDiskMapping(string root, out VolumeDiskMapping mapping, out int win32Error);
}

public sealed class KernelVolumeSource : IVolumeSource
{
    public static IVolumeSource Default { get; } = new KernelVolumeSource();

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int MaxExtents = 64;

    public IReadOnlyList<string> GetDriveRoots()
    {
        var mask = NativeVolumeMethods.GetLogicalDrives();
        var roots = new List<string>();
        for (var bit = 0; bit < 26; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                roots.Add($"{(char)('A' + bit)}:\\");
            }
        }

        return roots;
    }

    public bool TryGetVolumeInfo(string root, out VolumeLayout layout, out int win32Error)
    {
        var label = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        if (NativeVolumeMethods.GetVolumeInformation(
            root, label, (uint)label.Capacity, out _, out _, out _,
            fileSystem, (uint)fileSystem.Capacity)
            && NativeVolumeMethods.GetDiskFreeSpaceEx(root, out _, out var total, out var free))
        {
            layout = new VolumeLayout(root, label.ToString(), fileSystem.ToString(), total, free);
            win32Error = 0;
            return true;
        }

        layout = new VolumeLayout(root, string.Empty, string.Empty, 0, 0);
        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    public bool TryGetDiskMapping(string root, out VolumeDiskMapping mapping, out int win32Error)
    {
        mapping = new VolumeDiskMapping(root, [], 0);
        var devicePath = @"\\.\" + root.TrimEnd('\\');
        using var handle = NativeVolumeMethods.CreateFile(
            devicePath, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        var size = 8 + MaxExtents * 24;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeVolumeMethods.DeviceIoControl(
                handle, IoctlVolumeGetVolumeDiskExtents,
                IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            var returned = Math.Max(0, Marshal.ReadInt32(buffer));
            var readable = Math.Min(returned, MaxExtents);
            var disks = new List<int>(readable);
            for (var index = 0; index < readable; index++)
            {
                disks.Add(Marshal.ReadInt32(buffer, 8 + index * 24));
            }

            mapping = new VolumeDiskMapping(root, disks, returned);
            win32Error = 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal static class NativeVolumeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
