using System.Management;
// System.IO.EnumerationOptions arrives via implicit usings and makes the bare name ambiguous,
// which also breaks ManagementObjectSearcher overload resolution. Alias to the intended type.
using EnumerationOptions = System.Management.EnumerationOptions;

namespace Pitwall.Infrastructure.Windows;

public sealed record WmiQueryResult(
    bool Ok,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string ErrorCode,
    int HResult);

public interface IWmiSource
{
    Task<WmiQueryResult> QueryAsync(string scope, string query, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class WmiSource : IWmiSource
{
    public static IWmiSource Default { get; } = new WmiSource();

    public async Task<WmiQueryResult> QueryAsync(string scope, string query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return await Task.Run(() => Execute(scope, query, timeout), cancellationToken).ConfigureAwait(false);
    }

    private static WmiQueryResult Execute(string scope, string query, TimeSpan timeout)
    {
        try
        {
            var options = new EnumerationOptions
            {
                Timeout = timeout,
                ReturnImmediately = true,
                Rewindable = false,
            };
            using var searcher = new ManagementObjectSearcher(scope, query, options);
            using var results = searcher.Get();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (ManagementObject instance in results)
            {
                using (instance)
                {
                    rows.Add(Copy(instance));
                }
            }

            return new WmiQueryResult(true, rows, "ok", 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return new WmiQueryResult(false, [], "wmi.access_denied", exception.HResult);
        }
        catch (TimeoutException exception)
        {
            return new WmiQueryResult(false, [], "wmi.timeout", exception.HResult);
        }
        catch (ManagementException exception)
        {
            return exception.ErrorCode switch
            {
                ManagementStatus.InvalidQuery => new WmiQueryResult(false, [], "wmi.invalid_query", exception.HResult),
                ManagementStatus.InvalidClass or ManagementStatus.NotFound or ManagementStatus.NotSupported =>
                    new WmiQueryResult(false, [], "wmi.source_missing", exception.HResult),
                _ => new WmiQueryResult(false, [], "wmi.transport", exception.HResult),
            };
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            return new WmiQueryResult(false, [], "wmi.transport", exception.HResult);
        }
    }

    private static IReadOnlyDictionary<string, object?> Copy(ManagementObject instance)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyData property in instance.Properties)
        {
            row[property.Name] = Normalize(property.Value);
        }

        return row;
    }

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag,
        byte number => (long)number,
        sbyte number => (long)number,
        short number => (long)number,
        ushort number => (long)number,
        int number => (long)number,
        uint number => (long)number,
        long number => number,
        ulong number => number <= long.MaxValue ? (long)number : number,
        float number => (double)number,
        double number => number,
        Array items => items.Cast<object?>().Select(Normalize).ToArray(),
        _ => value.ToString(),
    };
}

public static class WmiRow
{
    public static string? GetString(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) ? value as string : null;

    public static IReadOnlyList<string> GetStrings(this IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null) return [];
        if (value is string single) return [single];
        if (value is Array items) return items.Cast<object?>().OfType<string>().ToArray();
        return [];
    }

    public static long? GetInteger(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) ? value switch
        {
            long number => number,
            ulong number => number <= long.MaxValue ? (long)number : null,
            int number => number,
            uint number => number,
            short number => number,
            ushort number => number,
            byte number => number,
            _ => null,
        } : null;

    public static IReadOnlyList<long> GetIntegers(this IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null) return [];
        if (value is Array items)
            return items.Cast<object?>().Select(static item => item switch
            {
                long number => (long?)number,
                int number => number,
                uint number => number,
                short number => number,
                ushort number => number,
                byte number => number,
                _ => null,
            }).OfType<long>().ToArray();
        return row.GetInteger(name) is { } single ? [single] : [];
    }

    public static bool? GetBoolean(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is bool flag ? flag : null;

    public static DateTimeOffset? GetDateTime(this IReadOnlyDictionary<string, object?> row, string name)
    {
        var text = row.GetString(name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
