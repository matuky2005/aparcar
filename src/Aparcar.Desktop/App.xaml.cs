using Microsoft.UI.Xaml;

namespace Aparcar.Desktop;

/// <summary>Punto de entrada WinUI 3 de Aparcar.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
