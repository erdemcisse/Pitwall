using System.Globalization;
using System.Resources;

namespace Pitwall.Resources;

public static class ResourceKeys
{
    public const string AppTitle = nameof(AppTitle);
    public const string PortableRootUnwritable = nameof(PortableRootUnwritable);
    public const string PortableRootRequired = nameof(PortableRootRequired);
    public const string UnknownReading = nameof(UnknownReading);
}

public static class ResourceCatalog
{
    private static readonly ResourceManager Manager = new("Pitwall.Resources.Strings", typeof(ResourceCatalog).Assembly);

    public static string GetRequired(string key, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        for (var candidate = culture; candidate != CultureInfo.InvariantCulture; candidate = candidate.Parent)
        {
            var value = Manager.GetResourceSet(candidate, createIfNotExists: true, tryParents: false)?.GetString(key);
            if (value is not null) return value;
        }
        return Manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), createIfNotExists: true, tryParents: false)?.GetString(key)
            ?? throw new MissingManifestResourceException("resource.missing_key:" + key);
    }
}
