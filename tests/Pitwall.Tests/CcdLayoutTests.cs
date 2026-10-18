using System.Runtime.InteropServices;
using Pitwall.Infrastructure.Windows;

namespace Pitwall.Tests;

public sealed class CcdLayoutTests
{
    // What this pins: that the managed definitions derive the documented native
    // strides (wingdi.h member lists, recorded in PROVENANCE.md). What it cannot
    // catch: a wrong premise shared by the code and these constants — both are
    // computed from the same hand transcription. The memory-safety argument does
    // not rest on this test; it rests on exact documented strides (no slack to
    // mis-derive) plus buffers sized from GetDisplayConfigBufferSizes counts with
    // returned-count validation before indexing.
    [Fact]
    public void Ccd_struct_sizes_match_the_documented_native_stride()
    {
        Assert.Equal(64, Marshal.SizeOf<NativeDisplayMethods.ModeInfo>());
        Assert.Equal(420, Marshal.SizeOf<NativeDisplayMethods.TargetDeviceName>());
        Assert.Equal(32, Marshal.SizeOf<NativeDisplayMethods.AdvancedColorInfo>());
        Assert.Equal(72, Marshal.SizeOf<NativeDisplayMethods.PathInfo>());
        Assert.Equal(20, Marshal.SizeOf<NativeDisplayMethods.PathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<NativeDisplayMethods.PathTargetInfo>());
    }

    [Theory]
    [InlineData("buffer", 5, "ccd.access_denied")]
    [InlineData("query", 5, "ccd.access_denied")]
    [InlineData("buffer_sizes", 122, "ccd.buffer_sizes_failed")]
    [InlineData("query", 87, "ccd.query_failed")]
    public void Ccd_stage_failures_map_to_specific_codes(string stage, int win32, string code)
    {
        var mapped = CcdDisplaySource.MapFailure(stage, win32);
        Assert.Equal(code, mapped.Code);
        Assert.Contains($"ccd.{stage}:win32:{win32}", mapped.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2u, 2u, 5u, 5u, true)]
    [InlineData(0u, 0u, 0u, 0u, true)]
    [InlineData(3u, 2u, 5u, 5u, false)]
    [InlineData(2u, 2u, 6u, 5u, false)]
    public void Ccd_returned_counts_beyond_the_buffers_are_refused(
        uint pathsIn, uint pathCount, uint modesIn, uint modeCount, bool expected) =>
        Assert.Equal(expected, CcdDisplaySource.ValidCounts(pathsIn, pathCount, modesIn, modeCount));
}
