using System.Globalization;
using Microsoft.UI.Xaml;
using Pitwall.Resources;

namespace Pitwall.App;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new Window { Title = ResourceCatalog.GetRequired(ResourceKeys.AppTitle, CultureInfo.CurrentUICulture) };
        window.Activate();
    }
}
