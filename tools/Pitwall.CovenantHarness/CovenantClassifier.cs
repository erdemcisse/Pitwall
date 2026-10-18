namespace Pitwall.CovenantHarness;

public static class CovenantClassifier
{
    public static CovenantStatus ClassifyBackgroundProcesses(int count, double threshold) =>
        ClassifyEqual(count, threshold, nameof(count));

    public static CovenantStatus ClassifyServices(int count, double threshold) =>
        ClassifyEqual(count, threshold, nameof(count));

    public static CovenantStatus ClassifyAutostartEntries(int count, double threshold) =>
        ClassifyEqual(count, threshold, nameof(count));

    public static CovenantStatus ClassifyMemoryMegabytes(double megabytes, double thresholdMegabytes)
    {
        RequireFinite(megabytes, nameof(megabytes));
        RequireThreshold(thresholdMegabytes, nameof(thresholdMegabytes));
        return megabytes <= thresholdMegabytes ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyIdleCpuPercent(double percent, double thresholdPercent)
    {
        RequireFinite(percent, nameof(percent));
        RequireThreshold(thresholdPercent, nameof(thresholdPercent));
        return percent <= thresholdPercent ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifySensorCpuPercent(double percent, double thresholdPercent)
    {
        RequireFinite(percent, nameof(percent));
        RequireThreshold(thresholdPercent, nameof(thresholdPercent));
        return percent <= thresholdPercent ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyColdLaunchSeconds(double seconds, double thresholdSeconds)
    {
        RequireFinite(seconds, nameof(seconds));
        RequireThreshold(thresholdSeconds, nameof(thresholdSeconds));
        return seconds <= thresholdSeconds ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyWarmLaunchSeconds(double seconds, double thresholdSeconds)
    {
        RequireFinite(seconds, nameof(seconds));
        RequireThreshold(thresholdSeconds, nameof(thresholdSeconds));
        return seconds <= thresholdSeconds ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyLaunch(
        double coldSeconds,
        double warmSeconds,
        double coldThresholdSeconds,
        double warmThresholdSeconds)
    {
        RequireFinite(coldSeconds, nameof(coldSeconds));
        RequireFinite(warmSeconds, nameof(warmSeconds));
        RequireThreshold(coldThresholdSeconds, nameof(coldThresholdSeconds));
        RequireThreshold(warmThresholdSeconds, nameof(warmThresholdSeconds));
        return coldSeconds <= coldThresholdSeconds && warmSeconds <= warmThresholdSeconds
            ? CovenantStatus.Pass
            : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyChart(
        double framesPerSecond,
        double overheadPercent,
        double fpsThreshold,
        double overheadThresholdPercent)
    {
        RequireFinite(framesPerSecond, nameof(framesPerSecond));
        RequireFinite(overheadPercent, nameof(overheadPercent));
        RequireThreshold(fpsThreshold, nameof(fpsThreshold));
        RequireThreshold(overheadThresholdPercent, nameof(overheadThresholdPercent));
        return framesPerSecond >= fpsThreshold && overheadPercent < overheadThresholdPercent
            ? CovenantStatus.Pass
            : CovenantStatus.Fail;
    }

    public static CovenantStatus ClassifyTelemetryState(int references, string? state)
    {
        if (references < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(references));
        }

        if (!string.Equals(state, "off", StringComparison.Ordinal))
        {
            throw new ArgumentException("covenant.telemetry_state_invalid", nameof(state));
        }

        return references == 0 ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    private static CovenantStatus ClassifyEqual(int measured, double threshold, string parameterName)
    {
        if (measured < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        if (!double.IsFinite(threshold) || threshold < 0 || threshold != Math.Truncate(threshold))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        return measured == (int)threshold ? CovenantStatus.Pass : CovenantStatus.Fail;
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("covenant.synthetic_non_finite", parameterName);
        }
    }

    private static void RequireThreshold(double threshold, string parameterName)
    {
        if (!double.IsFinite(threshold))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
