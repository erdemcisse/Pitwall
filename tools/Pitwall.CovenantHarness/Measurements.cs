using System.Buffers;
using System.Diagnostics;
using Microsoft.Win32;

namespace Pitwall.CovenantHarness;

public sealed record LaunchObservation(
    double ColdSeconds,
    double WarmSeconds,
    bool ColdWindowShown,
    bool WarmWindowShown,
    int ColdProcessId,
    int WarmProcessId);

public sealed record AppObservation(
    int ProcessId,
    double PrivateMegabytes,
    double WorkingSetMegabytes,
    double CpuPercent,
    int CpuSampleSeconds,
    int BackgroundProcesses,
    int SameNameProcesses);

public static class Measurements
{
    public const int CommitmentWindowSeconds = 300;

    private static readonly TimeSpan ChildStreamCleanupDeadline = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ChildStreamCleanupGracePeriod = TimeSpan.FromMilliseconds(100);

    private const int ChildStreamDrainBufferSize = 4096;

    public const string InteractiveDefinition =
        "Interactive means the WinUI window is shown: MainWindowHandle is non-zero and MainWindowTitle is 'Pitwall'.";

    public static async Task<LaunchObservation> MeasureLaunchAsync(
        string executablePath,
        TimeSpan windowTimeout,
        CancellationToken cancellationToken)
    {
        var cold = await LaunchOnceAsync(executablePath, windowTimeout, cancellationToken).ConfigureAwait(false);
        var warm = await LaunchOnceAsync(executablePath, windowTimeout, cancellationToken).ConfigureAwait(false);
        return new LaunchObservation(
            cold.ElapsedSeconds,
            warm.ElapsedSeconds,
            cold.WindowShown,
            warm.WindowShown,
            cold.ProcessId,
            warm.ProcessId);
    }

    public static async Task<AppObservation> MeasureRunningAppAsync(
        string executablePath,
        int cpuSampleSeconds,
        TimeSpan windowTimeout,
        TimeSpan settleDelay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (cpuSampleSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuSampleSeconds));
        }

        if (windowTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(windowTimeout));
        }

        if (settleDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settleDelay));
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("covenant.launch_failed");
        var stdoutReader = process.StandardOutput;
        var stderrReader = process.StandardError;
        var drainCancellation = new CancellationTokenSource();
        var stdoutDrain = DrainStreamAsync(stdoutReader, drainCancellation.Token);
        var stderrDrain = DrainStreamAsync(stderrReader, drainCancellation.Token);
        try
        {
            _ = await WaitForWindowAsync(process, windowTimeout, cancellationToken).ConfigureAwait(false);
            await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);

            process.Refresh();
            var privateMb = process.PrivateMemorySize64 / 1048576.0;
            var workingMb = process.WorkingSet64 / 1048576.0;
            var cpuPercent = await SampleCpuPercentAsync(process, cpuSampleSeconds, cancellationToken).ConfigureAwait(false);
            process.Refresh();
            var (background, sameName) = CountBackgroundProcesses(process);
            return new AppObservation(
                process.Id,
                privateMb,
                workingMb,
                cpuPercent,
                cpuSampleSeconds,
                background,
                sameName);
        }
        finally
        {
            CloseOwnedProcess(process);
            await DrainStreamsAsync(stdoutReader, stderrReader, stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);
        }
    }

    public static int CountAutostartEntries()
    {
        var count = 0;
        count += CountRunKeyMatches(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        count += CountRunKeyMatches(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce");
        count += CountStartupFolderMatches();
        return count;
    }

    public static int CountMatchingServices()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
            if (services is null)
            {
                return 0;
            }

            return services.GetSubKeyNames()
                .Count(static name => name.Contains("pitwall", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    public static int CountTelemetryReferences(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var keywords = new[] { "ApplicationInsights", "OpenTelemetry", "TelemetryClient", "TrackEvent", "Segment.Analytics", "Mixpanel", "Amplitude" };
        var count = 0;
        var directory = Path.Combine(repositoryRoot, "src");
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!IsScannedExtension(file))
                {
                    continue;
                }

                if (IsInsideBinOrObj(repositoryRoot, file))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var keyword in keywords)
                {
                    if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }
        }

        return count;
    }

    private static bool IsScannedExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideBinOrObj(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SingleLaunch(double ElapsedSeconds, bool WindowShown, int ProcessId);

    private static async Task<SingleLaunch> LaunchOnceAsync(
        string executablePath,
        TimeSpan windowTimeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory,
        };

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("covenant.launch_failed");
        var stdoutReader = process.StandardOutput;
        var stderrReader = process.StandardError;
        var drainCancellation = new CancellationTokenSource();
        var stdoutDrain = DrainStreamAsync(stdoutReader, drainCancellation.Token);
        var stderrDrain = DrainStreamAsync(stderrReader, drainCancellation.Token);
        try
        {
            var shown = await WaitForWindowAsync(process, windowTimeout, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new SingleLaunch(stopwatch.Elapsed.TotalSeconds, shown, process.Id);
        }
        finally
        {
            stopwatch.Stop();
            CloseOwnedProcess(process);
            await DrainStreamsAsync(stdoutReader, stderrReader, stdoutDrain, stderrDrain, drainCancellation).ConfigureAwait(false);
        }
    }

    internal static async Task DrainStreamsAsync(
        StreamReader stdoutReader,
        StreamReader stderrReader,
        Task stdoutDrain,
        Task stderrDrain,
        CancellationTokenSource drainCancellation)
    {
        ArgumentNullException.ThrowIfNull(stdoutReader);
        ArgumentNullException.ThrowIfNull(stderrReader);
        ArgumentNullException.ThrowIfNull(stdoutDrain);
        ArgumentNullException.ThrowIfNull(stderrDrain);
        ArgumentNullException.ThrowIfNull(drainCancellation);

        var drains = Task.WhenAll(stdoutDrain, stderrDrain);
        drainCancellation.CancelAfter(ChildStreamCleanupDeadline);
        try
        {
            await drains.WaitAsync(drainCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
        {
            DisposeReader(stdoutReader);
            DisposeReader(stderrReader);
            try
            {
                await drains.WaitAsync(ChildStreamCleanupGracePeriod).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or OperationCanceledException
                or IOException
                or UnauthorizedAccessException
                or TimeoutException)
            {
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or OperationCanceledException
            or IOException
            or UnauthorizedAccessException)
        {
        }
        finally
        {
            drainCancellation.Dispose();
        }
    }

    internal static async Task DrainStreamAsync(TextReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var buffer = ArrayPool<char>.Shared.Rent(ChildStreamDrainBufferSize);
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(0, ChildStreamDrainBufferSize), cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void DisposeReader(StreamReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> WaitForWindowAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (!process.HasExited
                && process.MainWindowHandle != IntPtr.Zero
                && string.Equals(process.MainWindowTitle, "Pitwall", StringComparison.Ordinal))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        process.Refresh();
        return !process.HasExited
            && process.MainWindowHandle != IntPtr.Zero
            && string.Equals(process.MainWindowTitle, "Pitwall", StringComparison.Ordinal);
    }

    private static async Task<double> SampleCpuPercentAsync(
        Process process,
        int sampleSeconds,
        CancellationToken cancellationToken)
    {
        process.Refresh();
        var startCpu = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(sampleSeconds), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var endCpu = process.HasExited ? startCpu : process.TotalProcessorTime;
        var cpuDeltaMs = (endCpu - startCpu).TotalMilliseconds;
        var elapsedMs = Math.Max(stopwatch.Elapsed.TotalMilliseconds, 1.0);
        var processorCount = Math.Max(Environment.ProcessorCount, 1);
        return (cpuDeltaMs / (elapsedMs * processorCount)) * 100.0;
    }

    private static (int Background, int SameName) CountBackgroundProcesses(Process main)
    {
        main.Refresh();
        var processName = main.ProcessName;
        var mainId = main.Id;
        string? mainPath = null;
        try
        {
            mainPath = main.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            mainPath = null;
        }

        var sameName = 0;
        var background = 0;
        foreach (var candidate in Process.GetProcessesByName(processName))
        {
            using (candidate)
            {
                if (candidate.Id == mainId)
                {
                    continue;
                }

                sameName++;
                try
                {
                    var candidatePath = candidate.MainModule?.FileName;
                    if (mainPath is not null && candidatePath is not null
                        && string.Equals(candidatePath, mainPath, StringComparison.OrdinalIgnoreCase))
                    {
                        background++;
                    }
                    else if (mainPath is null && candidatePath is null)
                    {
                        background++;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                }
            }
        }

        return (background, sameName);
    }

    private static int CountRunKeyMatches(string subKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return 0;
            }

            var matches = 0;
            foreach (var valueName in key.GetValueNames())
            {
                var data = key.GetValue(valueName)?.ToString() ?? string.Empty;
                if (data.Contains("pitwall", StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                }
            }

            return matches;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    private static int CountStartupFolderMatches()
    {
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startup) || !Directory.Exists(startup))
            {
                return 0;
            }

            return Directory.EnumerateFiles(startup)
                .Count(static path => Path.GetFileName(path).Contains("pitwall", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void CloseOwnedProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            try
            {
                if (process.CloseMainWindow())
                {
                    if (process.WaitForExit(5000))
                    {
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
