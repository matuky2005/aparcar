using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aparcar.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly AparcarDb _db;
    private Estadia? _estadiaSeleccionada;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Aparcar — Estacionamientos (demostración)";

        // V0.1: una sola cuenta de Windows. Fuera del directorio del paquete MSIX.
        var ruta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Aparcar", "Datos", "aparcar.db");
        _db = new AparcarDb(ruta);
        _db.Inicializar();

        RutaDatosText.Text = _db.RutaDb;
        Navegacion.SelectedItem = Navegacion.MenuItems[0];
        Refrescar();
    }

    private static string Dinero(long centavos) =>
        (centavos / 100m).ToString("C2",
            System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

    private void Navegacion_SelectionChanged(
        NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string pantalla)
            Mostrar(pantalla);
    }

    private void Mostrar(string pantalla)
    {
        InicioPanel.Visibility = pantalla == "inicio" ? Visibility.Visible : Visibility.Collapsed;
        IngresoPanel.Visibility = pantalla == "ingreso" ? Visibility.Visible : Visibility.Collapsed;
        SalidaPanel.Visibility = pantalla == "salida" ? Visibility.Visible : Visibility.Collapsed;
        PresentesPanel.Visibility = pantalla == "presentes" ? Visibility.Visible : Visibility.Collapsed;
        TarifasPanel.Visibility = pantalla == "tarifas" ? Visibility.Visible : Visibility.Collapsed;
        CajaPanel.Visibility = pantalla == "caja" ? Visibility.Visible : Visibility.Collapsed;
        LavaderoPanel.Visibility = pantalla == "lavadero" ? Visibility.Visible : Visibility.Collapsed;
        AbonadosPanel.Visibility = pantalla == "abonados" ? Visibility.Visible : Visibility.Collapsed;
        ReportesPanel.Visibility = pantalla == "reportes" ? Visibility.Visible : Visibility.Collapsed;
        ConfigPanel.Visibility = pantalla == "config" ? Visibility.Visible : Visibility.Collapsed;
        if (pantalla is "inicio" or "presentes") Refrescar();
    }

    private void IrIngreso_Click(object sender, RoutedEventArgs e) =>
        Navegacion.SelectedItem = Navegacion.MenuItems[1];

    private void IrSalida_Click(object sender, RoutedEventArgs e) =>
        Navegacion.SelectedItem = Navegacion.MenuItems[2];

    private void Actualizar_Click(object sender, RoutedEventArgs e) => Refrescar();

    private void Refrescar()
    {
        var presentes = _db.ListarActivas();
        TotalActivosText.Text = presentes.Count.ToString();
        var filas = presentes
            .Select(x => $"#{x.Ticket:D7}   {x.Patente,-12}  {x.TipoVehiculo,-11}  {x.IngresoUtc.ToLocalTime():dd/MM HH:mm}")
            .ToList();
        ResumenActivosList.ItemsSource = filas;
        PresentesList.ItemsSource = filas;
    }

    private void Ingresar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tipo = (TipoInput.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var ticket = _db.RegistrarIngreso(PatenteInput.Text, tipo, SectorInput.Text);
            Mensaje.Text = $"Ingreso registrado. Ticket #{ticket:D7}. Impresión pendiente.";
            PatenteInput.Text = "";
            Refrescar();
            PatenteInput.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Mensaje.Text = "Ingreso no registrado: " + ex.Message;
        }
    }

    private void Buscar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _estadiaSeleccionada = _db.BuscarActiva(BusquedaInput.Text);
            ConfirmarSalidaButton.IsEnabled = _estadiaSeleccionada is not null;
            if (_estadiaSeleccionada is null)
            {
                SalidaDetalleText.Text = "No se encontró una estadía activa.";
                SalidaImporteText.Text = "";
                return;
            }

            var duracion = DateTimeOffset.UtcNow - _estadiaSeleccionada.IngresoUtc;
            var importe = CalculadoraTarifas.CalcularCentavos(
                _estadiaSeleccionada.TipoVehiculo, duracion);
            SalidaDetalleText.Text =
                $"Ticket #{_estadiaSeleccionada.Ticket:D7} · {_estadiaSeleccionada.Patente}" +
                $" · {_estadiaSeleccionada.TipoVehiculo}\n" +
                $"Ingreso: {_estadiaSeleccionada.IngresoUtc.ToLocalTime():dd/MM/yyyy HH:mm}\n" +
                $"Tiempo aproximado: {(int)duracion.TotalMinutes} minutos";
            SalidaImporteText.Text = Dinero(importe) + " (simulado)";
            Mensaje.Text = "Importe ilustrativo; se recalcula al confirmar.";
        }
        catch (Exception ex)
        {
            _estadiaSeleccionada = null;
            ConfirmarSalidaButton.IsEnabled = false;
            Mensaje.Text = "Error al buscar: " + ex.Message;
        }
    }

    private void ConfirmarSalida_Click(object sender, RoutedEventArgs e)
    {
        if (_estadiaSeleccionada is null) return;
        ConfirmarSalidaButton.IsEnabled = false; // Evitar doble clic en la misma vista.
        try
        {
            var salida = _db.RegistrarSalidaPrueba(_estadiaSeleccionada.Id);
            Mensaje.Text = $"Salida de prueba #{salida.Ticket:D7} finalizada. " +
                           $"Importe ficticio: {Dinero(salida.ImporteSimuladoCentavos)}.";
            SalidaDetalleText.Text = "Estadía finalizada. Buscá otro vehículo.";
            SalidaImporteText.Text = "";
            BusquedaInput.Text = "";
            _estadiaSeleccionada = null;
            Refrescar();
        }
        catch (Exception ex)
        {
            Mensaje.Text = "No se confirmó la salida: " + ex.Message;
            _estadiaSeleccionada = null;
            SalidaDetalleText.Text = "Volvé a buscar la estadía para verificar su estado.";
        }
    }

    private void Respaldar_Click(object sender, RoutedEventArgs e)
    {
        try { Mensaje.Text = "Respaldo creado: " + _db.CrearRespaldo(); }
        catch (Exception ex) { Mensaje.Text = "Error al respaldar: " + ex.Message; }
    }
}
