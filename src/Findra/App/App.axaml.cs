using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Findra;

// Findra has no main window. It lives in the tray with a capsule on the desktop, so the
// desktop lifetime's default ShutdownMode (OnLastWindowClose) is wrong here - it would quit
// the whole application the moment the search card, which is not the "main window", is
// dismissed. OnExplicitShutdown means the process only exits when something asks it to
// (tray "Exit", or a future Shutdown() call), never as a side effect of a window closing.
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
