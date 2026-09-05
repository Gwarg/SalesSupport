using System.Windows;

namespace SalesSupport.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The saved theme goes in before StartupUri creates the first window.
        ThemeManager.Initialize();
        base.OnStartup(e);
    }
}
