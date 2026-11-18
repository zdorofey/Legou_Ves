using System.Windows;

namespace LegoKiosk;

public partial class App : Application
{
    public static bool DemoMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DemoMode = e.Args.Any(arg => string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }
}
