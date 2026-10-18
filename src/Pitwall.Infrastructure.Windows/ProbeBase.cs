using System.Diagnostics;
using Pitwall.Domain;

namespace Pitwall.Infrastructure.Windows;

public abstract class ProbeBase : IInventoryProbe
{
    public abstract string ProbeId { get; }

    protected abstract FactSource Source { get; }

    protected virtual string CodePrefix => ProbeId;

    public async Task<ProbeOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await CollectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ProbeOutcome.Failed(
                ProbeId, FactQuality.Unavailable, Elapsed(started), Source,
                ProbeErrorKind.Thrown, "probe.unhandled", exception.GetType().Name);
        }
    }

    protected abstract Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken);

    protected ProbeOutcome Finish(
        List<ObservedFact> facts,
        long started,
        int invalidRows = 0,
        string? note = null,
        (FactQuality Coverage, ProbeErrorKind Kind, string Code)? emptyFallback = null)
    {
        var duration = Elapsed(started);
        if (invalidRows > 0)
        {
            return ProbeOutcome.Partial(
                ProbeId, [.. facts], duration, Source,
                ProbeErrorKind.InvalidPayload, CodePrefix + ".invalid_row", note);
        }

        if (facts.Any(static fact => fact.Reading.Match(static (quality, _) => quality == FactQuality.Valid, static _ => false)))
        {
            return ProbeOutcome.Completed(ProbeId, [.. facts], duration, Source, note);
        }

        if (emptyFallback is { } fallback)
        {
            return ProbeOutcome.Failed(ProbeId, fallback.Coverage, [.. facts], duration, Source, fallback.Kind, fallback.Code, note);
        }

        return ProbeOutcome.Failed(
            ProbeId, FactQuality.Unavailable, [.. facts], duration, Source,
            ProbeErrorKind.SourceMissing, CodePrefix + ".no_data", note);
    }

    protected static long Start() => Stopwatch.GetTimestamp();

    protected static TimeSpan Elapsed(long started) =>
        Stopwatch.GetElapsedTime(started);

    protected static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    protected static (FactQuality Coverage, ProbeErrorKind Kind, string Code) WmiFallback(WmiQueryResult result) =>
        result.ErrorCode switch
        {
            "wmi.access_denied" => (FactQuality.PermissionDenied, ProbeErrorKind.AccessDenied, result.ErrorCode),
            "wmi.timeout" => (FactQuality.Unavailable, ProbeErrorKind.Timeout, result.ErrorCode),
            _ => (FactQuality.Unavailable, ProbeErrorKind.SourceMissing, result.ErrorCode),
        };

    protected static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(8);

    protected static readonly UnitMetadata NoUnits = new(null, null, null);
    protected static readonly UnitMetadata Bytes = new("bytes", "bytes", null);
    protected static readonly UnitMetadata Count = new("count", "count", null);
    protected static readonly UnitMetadata Percent = new("percent", "percent", null);
    protected static readonly UnitMetadata Megahertz = new("MHz", "MHz", null);
    protected static readonly UnitMetadata Celsius = new("tenth-kelvin", "celsius", "p0-06-v1");
    protected static readonly UnitMetadata Seconds = new("seconds", "seconds", null);
    protected static readonly UnitMetadata IsoTime = new("cim-datetime", "iso-8601", "p0-06-v1");
}
