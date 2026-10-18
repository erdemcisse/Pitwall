using System.Globalization;
using Pitwall.Infrastructure.Windows;
using Pitwall.Scanning;

namespace Pitwall.InventoryHarness;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        return await RunAsync(args, lifetime.Token).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? outputDirectory = null;
        var probeTimeoutSeconds = (int)QuickScanCoordinator.DefaultProbeTimeout.TotalSeconds;
        var deadlineSeconds = (int)QuickScanRunner.DefaultOverallDeadline.TotalSeconds;

        try
        {
            var positionals = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (string.Equals(token, "--probe-timeout-seconds", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out probeTimeoutSeconds)
                        || probeTimeoutSeconds <= 0 || probeTimeoutSeconds > 60)
                    {
                        await Console.Error.WriteLineAsync("inventory.invalid_probe_timeout").ConfigureAwait(false);
                        return 2;
                    }
                }
                else if (string.Equals(token, "--deadline-seconds", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out deadlineSeconds)
                        || deadlineSeconds <= 0 || deadlineSeconds > 600)
                    {
                        await Console.Error.WriteLineAsync("inventory.invalid_deadline").ConfigureAwait(false);
                        return 2;
                    }
                }
                else if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    await Console.Error.WriteLineAsync("inventory.unknown_option:" + token).ConfigureAwait(false);
                    return 2;
                }
                else
                {
                    positionals.Add(token);
                }
            }

            if (positionals.Count != 1)
            {
                await Console.Error.WriteLineAsync("inventory.usage:Pitwall.InventoryHarness <outputDirectory> [options]").ConfigureAwait(false);
                return 2;
            }

            outputDirectory = positionals[0];
            var runner = new QuickScanRunner();
            var result = await runner.RunAsync(
                WindowsProbeCatalog.Create(),
                Path.GetFullPath(outputDirectory),
                TimeSpan.FromSeconds(deadlineSeconds),
                TimeSpan.FromSeconds(probeTimeoutSeconds),
                QuickScanCoordinator.MaxConcurrency,
                null,
                cancellationToken).ConfigureAwait(false);

            if (result.ErrorCode is not null)
            {
                await Console.Error.WriteLineAsync(result.ErrorCode).ConfigureAwait(false);
            }

            if (result.ExitCode == 0)
            {
                await Console.Out.WriteLineAsync(
                    FormattableString.Invariant($"inventory.done {result.RunId:D} {result.SnapshotPath}").AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("inventory.cancelled").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("inventory.error:" + exception.Message).ConfigureAwait(false);
            return 2;
        }
    }
}
