using System.Runtime.InteropServices;

namespace Pitwall.Infrastructure.Windows;

public sealed record DisplayPathInfo(
    string AdapterLuid,
    long SourceId,
    long TargetId,
    string? MonitorName,
    string? MonitorPath,
    long? Width,
    long? Height,
    double? RefreshHz,
    long? AdvancedColorValue);

public sealed record DisplayQueryResult(
    bool Ok,
    IReadOnlyList<DisplayPathInfo> Paths,
    string? ErrorCode,
    string? Detail);

public interface IDisplaySource
{
    DisplayQueryResult QueryActivePaths();
}

public sealed class CcdDisplaySource : IDisplaySource
{
    public static IDisplaySource Default { get; } = new CcdDisplaySource();

    private const uint ActivePathsOnly = 0x00000002;
    private const int AccessDeniedCode = 5;
    private const uint TargetDeviceName = 2;
    private const uint AdvancedColorInfo = 9;
    private const uint ModeInfoTarget = 2;
    private const uint ModeIndexInvalid = 0xFFFFFFFF;
    private const int Success = 0;

    public DisplayQueryResult QueryActivePaths()
    {
        var code = NativeDisplayMethods.GetDisplayConfigBufferSizes(ActivePathsOnly, out var pathCount, out var modeCount);
        if (code != Success)
        {
            return Fail(MapFailure("buffer_sizes", code));
        }

        if (pathCount == 0)
        {
            return new DisplayQueryResult(true, [], null, null);
        }

        var pathArray = new NativeDisplayMethods.PathInfo[pathCount];
        var modeArray = new NativeDisplayMethods.ModeInfo[modeCount];
        var pathsIn = pathCount;
        var modesIn = modeCount;
        code = NativeDisplayMethods.QueryDisplayConfig(ActivePathsOnly, ref pathsIn, pathArray, ref modesIn, modeArray, IntPtr.Zero);
        if (code != Success)
        {
            return Fail(MapFailure("query", code));
        }

        if (!ValidCounts(pathsIn, pathCount, modesIn, modeCount))
        {
            return new DisplayQueryResult(false, [], "ccd.inconsistent_counts",
                FormattableString.Invariant($"paths:{pathsIn}/{pathCount};modes:{modesIn}/{modeCount}"));
        }

        var collected = new List<DisplayPathInfo>();
        for (var index = 0; index < pathsIn; index++)
        {
            collected.Add(ReadPath(pathArray[index], modeArray, modesIn));
        }

        return new DisplayQueryResult(true, collected, null, null);
    }

    internal static (string Code, string Detail) MapFailure(string stage, int win32) =>
        win32 == AccessDeniedCode
            ? ("ccd.access_denied", FormattableString.Invariant($"ccd.{stage}:win32:5"))
            : (FormattableString.Invariant($"ccd.{stage}_failed"), FormattableString.Invariant($"ccd.{stage}:win32:{win32}"));

    internal static bool ValidCounts(uint pathsIn, uint pathCount, uint modesIn, uint modeCount) =>
        pathsIn <= pathCount && modesIn <= modeCount;

    private static DisplayQueryResult Fail((string Code, string Detail) mapped) =>
        new(false, [], mapped.Code, mapped.Detail);

    private static DisplayPathInfo ReadPath(
        NativeDisplayMethods.PathInfo path,
        NativeDisplayMethods.ModeInfo[] modes,
        uint modeCount)
    {
        var luid = FormatLuid(path.Target.AdapterId);
        long? width = null;
        long? height = null;
        if (path.Target.ModeInfoIdx != ModeIndexInvalid
            && path.Target.ModeInfoIdx < modeCount
            && path.Target.ModeInfoIdx < modes.Length
            && modes[path.Target.ModeInfoIdx].InfoType == ModeInfoTarget)
        {
            width = modes[path.Target.ModeInfoIdx].ActiveWidth;
            height = modes[path.Target.ModeInfoIdx].ActiveHeight;
        }

        double? refresh = path.Target.RefreshRate.Denominator == 0
            ? null
            : path.Target.RefreshRate.Numerator / (double)path.Target.RefreshRate.Denominator;

        var name = new NativeDisplayMethods.TargetDeviceName
        {
            InfoType = TargetDeviceName,
            Size = (uint)Marshal.SizeOf<NativeDisplayMethods.TargetDeviceName>(),
            AdapterId = path.Target.AdapterId,
            Id = path.Target.Id,
            FriendlyName = string.Empty,
            DevicePath = string.Empty,
        };
        string? monitorName = null;
        string? monitorPath = null;
        if (NativeDisplayMethods.DisplayConfigGetDeviceInfo(ref name) == Success)
        {
            monitorName = string.IsNullOrWhiteSpace(name.FriendlyName) ? null : name.FriendlyName.TrimEnd('\0');
            monitorPath = string.IsNullOrWhiteSpace(name.DevicePath) ? null : name.DevicePath.TrimEnd('\0');
        }

        var color = new NativeDisplayMethods.AdvancedColorInfo
        {
            InfoType = AdvancedColorInfo,
            Size = (uint)Marshal.SizeOf<NativeDisplayMethods.AdvancedColorInfo>(),
            AdapterId = path.Target.AdapterId,
            Id = path.Target.Id,
        };
        long? colorValue = NativeDisplayMethods.DisplayConfigGetDeviceInfo(ref color) == Success
            ? color.Value
            : null;

        return new DisplayPathInfo(
            luid, path.Source.Id, path.Target.Id,
            monitorName, monitorPath, width, height, refresh, colorValue);
    }

    private static string FormatLuid(NativeDisplayMethods.Luid luid) =>
        $"luid:{luid.HighPart:X8}{luid.LowPart:X8}";
}

internal static class NativeDisplayMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo Source;
        public PathTargetInfo Target;
        public uint Flags;
    }

    // sizeof(DISPLAYCONFIG_MODE_INFO) is 64: a 16-byte header (type, id, adapter
    // LUID) plus a 48-byte mode union (target mode = video-signal info: 40 bytes
    // of timing plus a 4-byte standard union and scan-line ordering, padded to 48).
    // Only the target-mode active-size window at offsets 40/44 is read; Tail pads
    // the element to the exact native stride so QueryDisplayConfig writes within
    // the managed array.
    [StructLayout(LayoutKind.Explicit)]
    internal struct ModeInfo
    {
        [FieldOffset(0)]
        public uint InfoType;
        [FieldOffset(40)]
        public uint ActiveWidth;
        [FieldOffset(44)]
        public uint ActiveHeight;
        [FieldOffset(60)]
        public uint Tail;
    }

    // Sequential layout derives the documented DISPLAYCONFIG_TARGET_DEVICE_NAME
    // offsets: header (20) + flags struct (4) + outputTechnology (4) + edid ids
    // (2+2) + connectorInstance (4), then monitorFriendlyDeviceName[64] at 36 and
    // monitorDevicePath[128] at 164, total 420. The Size field must equal the
    // native size or DisplayConfigGetDeviceInfo refuses the packet.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public uint InfoType;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DevicePath;
    }

    // sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO) is 32: header (20) + value
    // union (4) + colorEncoding (4) + bitsPerColorChannel (4). The trailing two
    // fields are stride only; their decode is intentionally not implemented.
    [StructLayout(LayoutKind.Explicit)]
    internal struct AdvancedColorInfo
    {
        [FieldOffset(0)]
        public uint InfoType;
        [FieldOffset(4)]
        public uint Size;
        [FieldOffset(8)]
        public Luid AdapterId;
        [FieldOffset(16)]
        public uint Id;
        [FieldOffset(20)]
        public uint Value;
        [FieldOffset(24)]
        public uint ColorEncoding;
        [FieldOffset(28)]
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [In, Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] ModeInfo[] modeArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo requestPacket);
}
