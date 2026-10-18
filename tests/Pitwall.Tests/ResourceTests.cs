using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using Pitwall.ResourceLint;
using Pitwall.Resources;

namespace Pitwall.Tests;

public sealed class ResourceTests
{
    [Fact]
    public void German_English_parent_and_fallback_are_deterministic()
    {
        var en = ResourceCatalog.GetRequired(ResourceKeys.PortableRootUnwritable, CultureInfo.GetCultureInfo("en"));
        var de = ResourceCatalog.GetRequired(ResourceKeys.PortableRootUnwritable, CultureInfo.GetCultureInfo("de"));
        Assert.StartsWith("The portable", en);
        Assert.StartsWith("Der portable", de);
        Assert.Equal(de, ResourceCatalog.GetRequired(ResourceKeys.PortableRootUnwritable, CultureInfo.GetCultureInfo("de-CH")));
        Assert.Equal(en, ResourceCatalog.GetRequired(ResourceKeys.PortableRootUnwritable, CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal(en, ResourceCatalog.GetRequired(ResourceKeys.PortableRootUnwritable, CultureInfo.InvariantCulture));
        Assert.Equal("—", ResourceCatalog.GetRequired(ResourceKeys.UnknownReading, CultureInfo.GetCultureInfo("de")));
        Assert.Throws<MissingManifestResourceException>(() => ResourceCatalog.GetRequired("Nonexistent", CultureInfo.GetCultureInfo("de")));
        var root = Path.Combine(Fixtures.RepositoryRoot, "src", "Pitwall.Resources");
        string[] Keys(string name) => XDocument.Load(Path.Combine(root, name)).Root!.Elements("data").Select(element => element.Attribute("name")!.Value).Order().ToArray();
        Assert.Equal(Keys("Strings.en.resx"), Keys("Strings.de.resx"));
        Assert.Equal(Keys("Strings.resx"), Keys("Strings.en.resx"));
    }

    [Fact]
    public async Task Source_lint_passes_and_bad_fixtures_return_nonzero()
    {
        var artifact = Path.Combine(Fixtures.RepositoryRoot, "artifacts", "phase0a", "resource-lint-results.json");
        Assert.Equal(0, await LintCli.RunAsync([Path.Combine(Fixtures.RepositoryRoot, "src"), artifact]));
        var root = Fixtures.Sandbox();
        await File.WriteAllTextAsync(Path.Combine(root, "Bad.cs"), "class Bad { void Render() { var label = \"Hardcoded text\"; view.Text = label; } }");
        Assert.Equal(1, await LintCli.RunAsync([root, Path.Combine(root, "lint.json")]));
        await File.WriteAllTextAsync(Path.Combine(root, "Bad.xaml"), "<TextBlock Text=\"Hardcoded label\" />");
        Assert.Equal(2, SourceLint.Scan(root).Count);
    }

    [Theory]
    [InlineData("class C { void M() { view.Text = $\"Temperature {reading}\"; } }")]
    [InlineData("class C { const string Caption = \"English\"; void M() { view.Title = Caption; } }")]
    [InlineData("class C { void M() { AutomationProperties.SetName(button, \"Open\"); } }")]
    [InlineData("class C { public string Title { get; } = \"Title\"; }")]
    [InlineData("class C { public string Text => \"Text\"; }")]
    [InlineData("class C { public string Header { get { return \"Header\"; } } }")]
    public async Task Lint_catches_interpolation_constants_and_automation_literals(string code)
    {
        var root = Fixtures.Sandbox();
        await File.WriteAllTextAsync(Path.Combine(root, "Bad.cs"), code);
        Assert.NotEmpty(SourceLint.Scan(root));
    }
}
