using Microsoft.UI.Xaml;

namespace Aparcar.Desktop;

public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Aparcar — Estacionamientos (demostración)";
        Content = new MainWindowView(this);
    }
}
