using System;
using System.IO;
using System.Drawing.Printing;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Aparcar.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly AparcarDb _db;
    private Estadia? _estadiaSeleccionada;
    private bool _guardandoIngreso;
    private string? _impresora;
    private int _anchoTicket = 80;

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
        CargarImpresoras();
        AutoImprimirCheck.IsChecked = _db.ObtenerPreferencia("AutoImprimir") != "false";
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
        if (pantalla == "ingreso") PatenteInput.Focus(FocusState.Programmatic);
        if (pantalla == "salida") BusquedaInput.Focus(FocusState.Programmatic);
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
        if (_guardandoIngreso) return;
        _guardandoIngreso = true;
        GuardarIngresoButton.IsEnabled = false;
        try
        {
            var tipo = (TipoInput.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var numero = _db.RegistrarIngreso(PatenteInput.Text, tipo, SectorInput.Text);
            var ticket = _db.ObtenerTicket(numero);
            TicketPreviewText.Text = $"Ticket {ticket.Numero:D7} · {ticket.Patente} · {ticket.TipoVehiculo} · " +
                                    $"{ticket.FechaIngresoUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
            Mensaje.Text = $"Ingreso guardado. Ticket #{numero:D7}.";
            PatenteInput.Text = "";
            Refrescar();
            // Nunca volver a crear una estadía por un problema de impresión.
            if (AutoImprimirCheck.IsChecked == true)
                ImprimirTicketSeguro(numero);
        }
        catch (Exception ex)
        {
            Mensaje.Text = "Ingreso no registrado: " + ex.Message;
        }
        finally
        {
            _guardandoIngreso = false;
            GuardarIngresoButton.IsEnabled = true;
            PatenteInput.Focus(FocusState.Programmatic);
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


    private void CargarImpresoras()
    {
        try
        {
            ImpresorasCombo.Items.Clear();
            foreach (string nombre in PrinterSettings.InstalledPrinters)
                ImpresorasCombo.Items.Add(nombre);
            _impresora = _db.ObtenerPreferencia("Impresora");
            _anchoTicket = _db.ObtenerPreferencia("PapelMm") == "58" ? 58 : 80;
            AnchoTicketCombo.SelectedIndex = _anchoTicket == 58 ? 0 : 1;
            if (!string.IsNullOrWhiteSpace(_impresora))
            {
                foreach (var p in ImpresorasCombo.Items)
                    if (string.Equals(p?.ToString(), _impresora, StringComparison.OrdinalIgnoreCase))
                    {
                        ImpresorasCombo.SelectedItem = p;
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Mensaje.Text = "No se pudo enumerar las impresoras de Windows: " + ex.Message;
        }
    }

    private void GuardarImpresora_Click(object sender, RoutedEventArgs e)
    {
        var elegida = ImpresorasCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(elegida))
        {
            Mensaje.Text = "Seleccioná una impresora instalada en Windows.";
            return;
        }
        try
        {
            var mm = AnchoTicketCombo.SelectedIndex == 0 ? 58 : 80;
            _db.GuardarPreferencia("Impresora", elegida);
            _db.GuardarPreferencia("PapelMm", mm.ToString());
            _db.GuardarPreferencia("AutoImprimir", AutoImprimirCheck.IsChecked == true ? "true" : "false");
            _impresora = elegida;
            _anchoTicket = mm;
            Mensaje.Text = $"Impresora configurada: {elegida} · {mm} mm.";
        }
        catch (Exception ex) { Mensaje.Text = "No se pudo guardar la impresora: " + ex.Message; }
    }

    private void ImprimirTicketSeguro(long numero)
    {
        var ticket = _db.ObtenerTicket(numero);
        var destino = _impresora ?? "";
        try
        {
            TicketPrinter.ImprimirIngreso(ticket, destino, _anchoTicket);
            _db.RegistrarImpresion(ticket.EstadiaId, destino, true, "Enviado al controlador de impresión");
            Mensaje.Text = $"Ingreso #{numero:D7} guardado. Ticket enviado a {destino}.";
        }
        catch (Exception ex)
        {
            // La persistencia del ingreso ya está confirmada; solo se reporta
            // el fallo de impresión, sin iniciar un segundo registro.
            try { _db.RegistrarImpresion(ticket.EstadiaId, destino, false, ex.Message); }
            catch { /* No tapar el error original del controlador de impresión. */ }
            Mensaje.Text = $"Ingreso #{numero:D7} GUARDADO, pero NO impreso: {ex.Message}. Reimprimilo desde Configuración.";
        }
    }

    private void ReimprimirTicket_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(TicketReimpresionInput.Text.Trim(), out var numero) || numero <= 0)
        {
            Mensaje.Text = "Ingresá un número de ticket válido para reimprimir.";
            return;
        }
        try { ImprimirTicketSeguro(numero); }
        catch (Exception ex) { Mensaje.Text = "No se pudo reimprimir: " + ex.Message; }
    }

    private void IrConfig_Click(object sender, RoutedEventArgs e) =>
        Navegacion.SelectedItem = Navegacion.MenuItems[9];

    private void PatenteInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        TipoInput.Focus(FocusState.Programmatic);
    }

    private void TipoInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || TipoInput.IsDropDownOpen) return;
        e.Handled = true;
        SectorInput.Focus(FocusState.Programmatic);
    }

    private void SectorInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        GuardarIngresoButton.Focus(FocusState.Programmatic);
    }

    private void BusquedaInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Buscar_Click(sender, new RoutedEventArgs());
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F2)
            Navegacion.SelectedItem = Navegacion.MenuItems[1];
        else if (e.Key == VirtualKey.F4)
            Navegacion.SelectedItem = Navegacion.MenuItems[2];
        else if (e.Key == VirtualKey.F8)
            Navegacion.SelectedItem = Navegacion.MenuItems[3];
        else if (e.Key == VirtualKey.F10 && IngresoPanel.Visibility == Visibility.Visible)
            Ingresar_Click(sender, new RoutedEventArgs());
        else return;
        e.Handled = true;
    }

    private void Respaldar_Click(object sender, RoutedEventArgs e)
    {
        try { Mensaje.Text = "Respaldo creado: " + _db.CrearRespaldo(); }
        catch (Exception ex) { Mensaje.Text = "Error al respaldar: " + ex.Message; }
    }
}
