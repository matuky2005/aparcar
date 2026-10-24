using System;
using System.IO;
using System.Drawing.Printing;
using System.Linq;
using System.Globalization;
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
    private CotizacionOperativa? _cotizacion;
    private Guid _claveCobro = Guid.Empty;

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
        TipoTarifaCombo.SelectedIndex = 0;
        ActualizarCaja();
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
        if (pantalla == "caja") ActualizarCaja();
        if (pantalla == "tarifas") MostrarTarifaSeleccionada();
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
            _cotizacion = null;
            _claveCobro = Guid.Empty;
            ConfirmarSalidaButton.IsEnabled = false;
            _estadiaSeleccionada = _db.BuscarActiva(BusquedaInput.Text);
            if (_estadiaSeleccionada is null)
            {
                SalidaDetalleText.Text = "No se encontró una estadía activa.";
                SalidaImporteText.Text = "";
                CotizacionVigenciaText.Text = "";
                return;
            }

            _cotizacion = _db.CotizarSalida(_estadiaSeleccionada.Id);
            _claveCobro = Guid.NewGuid();
            var diferencia = _cotizacion.FechaCalculoUtc - _estadiaSeleccionada.IngresoUtc;
            SalidaDetalleText.Text =
                $"Ticket #{_cotizacion.Ticket:D7} · {_cotizacion.Patente}" +
                $" · {_cotizacion.TipoVehiculo}\n" +
                $"Ingreso: {_estadiaSeleccionada.IngresoUtc.ToLocalTime():dd/MM/yyyy HH:mm}\n" +
                $"Tiempo aproximado: {(int)diferencia.TotalMinutes} minutos" +
                $"\nTarifa versión {_cotizacion.TarifaVersion}";
            SalidaImporteText.Text = Dinero(_cotizacion.ImporteCentavos);
            CotizacionVigenciaText.Text = "Cotización válida hasta " +
                _cotizacion.VenceUtc.ToLocalTime().ToString("HH:mm:ss");
            ConfirmarSalidaButton.IsEnabled = _cotizacion.TurnoId.HasValue;
            Mensaje.Text = _cotizacion.TurnoId.HasValue
                ? "Contá el efectivo y confirmá el cobro."
                : "Para cobrar primero abrí un turno en Caja.";
            EfectivoRecibidoInput.Text = "";
            VueltoPrevistoText.Text = "";
            EfectivoRecibidoInput.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            _cotizacion = null;
            _claveCobro = Guid.Empty;
            _estadiaSeleccionada = null;
            ConfirmarSalidaButton.IsEnabled = false;
            Mensaje.Text = "Error al buscar: " + ex.Message;
        }
    }

    private void ConfirmarSalida_Click(object sender, RoutedEventArgs e)
    {
        if (_cotizacion is null || _claveCobro == Guid.Empty) return;
        long entregado;
        try { entregado = LeerImporte(EfectivoRecibidoInput.Text); }
        catch (Exception ex)
        {
            Mensaje.Text = "Efectivo recibido: " + ex.Message;
            return;
        }

        ConfirmarSalidaButton.IsEnabled = false;
        try
        {
            var cobro = _db.ConfirmarEfectivo(_cotizacion, entregado, _claveCobro);
            Mensaje.Text = $"Cobro #{cobro.Ticket:D7} registrado: {Dinero(cobro.ImporteCentavos)}" +
                           $" · Vuelto {Dinero(cobro.VueltoCentavos)}.";
            SalidaDetalleText.Text = "Cobro y salida confirmados. Buscá el siguiente vehículo.";
            SalidaImporteText.Text = "";
            CotizacionVigenciaText.Text = "";
            VueltoPrevistoText.Text = "";
            BusquedaInput.Text = "";
            EfectivoRecibidoInput.Text = "";
            _cotizacion = null;
            _estadiaSeleccionada = null;
            _claveCobro = Guid.Empty;
            Refrescar();
            ActualizarCaja();
            BusquedaInput.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Mensaje.Text = "No se confirmó la operación: " + ex.Message +
                           " Revisá el estado antes de volver a cobrar.";
            ConfirmarSalidaButton.IsEnabled = false;
            // Para evitar cobrar un importe antiguo se fuerza una nueva cotización.
            // Si ya se guardó, el siguiente escaneo encontrará la estadía finalizada.
            _cotizacion = null;
        }
    }

    private static long LeerImporte(string texto)
    {
        if (!decimal.TryParse(texto.Trim(), NumberStyles.Number,
            CultureInfo.GetCultureInfo("es-AR"), out var valor) || valor < 0 ||
            decimal.Round(valor, 2) != valor)
            throw new ArgumentException("Usá un monto válido (hasta dos decimales, ej. 1500,50).");
        return checked((long)(valor * 100m));
    }

    private static string EditarImporte(long centavos) =>
        (centavos / 100m).ToString("0.##", CultureInfo.GetCultureInfo("es-AR"));

    private void EfectivoRecibidoInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ConfirmarSalidaButton.Focus(FocusState.Programmatic);
    }

    private void EfectivoRecibidoInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VueltoPrevistoText is null) return;
        if (_cotizacion is null) { VueltoPrevistoText.Text = ""; return; }
        try
        {
            var recibido=LeerImporte(EfectivoRecibidoInput.Text);
            VueltoPrevistoText.Text = recibido >= _cotizacion.ImporteCentavos
                ? "Vuelto a entregar: " + Dinero(recibido-_cotizacion.ImporteCentavos)
                : "Faltan " + Dinero(_cotizacion.ImporteCentavos-recibido);
        }
        catch { VueltoPrevistoText.Text = ""; }
    }

    private void TipoTarifaCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e) => MostrarTarifaSeleccionada();

    private void MostrarTarifaSeleccionada()
    {
        if (_db is null || TipoTarifaCombo.SelectedItem is not ComboBoxItem c) return;
        var tipo = c.Content?.ToString() ?? "Auto";
        try
        {
            var tarifa=_db.ListarTarifas().Single(t=>t.TipoVehiculo==tipo);
            VersionTarifaText.Text = $"Versión actual {tarifa.Version} · cambios solo para próximos ingresos";
            PrimeraHoraInput.Text=EditarImporte(tarifa.PrimeraHoraCentavos);
            FraccionPrecioInput.Text=EditarImporte(tarifa.FraccionCentavos);
            MinutosFraccionInput.Text=tarifa.MinutosFraccion.ToString();
            TopeDiaInput.Text=EditarImporte(tarifa.Tope24HorasCentavos);
            CortesiaInput.Text=tarifa.CortesiaMinutos.ToString();
        }
        catch (Exception ex) { Mensaje.Text = "No se pudo cargar la tarifa: "+ex.Message; }
    }

    private void GuardarTarifa_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tipo=(TipoTarifaCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if(!int.TryParse(MinutosFraccionInput.Text,out var fraccion) ||
               !int.TryParse(CortesiaInput.Text,out var cortesia))
                throw new ArgumentException("Minutos y cortesía deben ser números enteros.");
            var tarifa=_db.CambiarTarifa(tipo,LeerImporte(PrimeraHoraInput.Text),
                LeerImporte(FraccionPrecioInput.Text),fraccion,
                LeerImporte(TopeDiaInput.Text),cortesia);
            Mensaje.Text=$"Tarifa de {tipo} guardada. Versión {tarifa.Version}.";
            MostrarTarifaSeleccionada();
        }
        catch(Exception ex) { Mensaje.Text = "No se guardó la tarifa: "+ex.Message; }
    }

    private void ActualizarCaja_Click(object sender, RoutedEventArgs e) => ActualizarCaja();

    private void ActualizarCaja()
    {
        try
        {
            var turno = _db.ObtenerTurnoAbierto();
            if (turno is null)
            {
                CajaEstadoText.Text = "No hay turno abierto.";
                AbrirCajaButton.IsEnabled = true;
                CerrarCajaButton.IsEnabled = false;
            }
            else
            {
                CajaEstadoText.Text = $"Turno de {turno.Operador} desde {turno.AperturaUtc.ToLocalTime():dd/MM HH:mm}" +
                    $"\nFondo: {Dinero(turno.FondoInicialCentavos)} · Cobros: {Dinero(turno.CobradoCentavos)}" +
                    $" · Efectivo esperado: {Dinero(turno.EsperadoCentavos)}";
                AbrirCajaButton.IsEnabled = false;
                CerrarCajaButton.IsEnabled = true;
            }
            CobrosCajaList.ItemsSource = _db.ListarCobros(60).Select(c =>
                $"#{c.Ticket:D7} · {c.Patente} · {Dinero(c.ImporteCentavos)}" +
                $" · {c.FechaUtc.ToLocalTime():dd/MM HH:mm} · {c.Operador}").ToList();
        }
        catch (Exception ex) { Mensaje.Text = "Error al cargar caja: "+ex.Message; }
    }

    private void AbrirCaja_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var turno = _db.AbrirTurno(OperadorCajaInput.Text,
                LeerImporte(FondoInicialInput.Text));
            Mensaje.Text=$"Caja abierta por {turno.Operador}.";
            UltimoArqueoText.Text="";
            ActualizarCaja();
        }
        catch(Exception ex) { Mensaje.Text="No se abrió la caja: "+ex.Message; }
    }

    private async void CerrarCaja_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var turno=_db.ObtenerTurnoAbierto() ??
                throw new InvalidOperationException("No hay turno abierto.");
            var contado=LeerImporte(EfectivoContadoInput.Text);
            var dialogo=new ContentDialog
            {
                Title="Confirmar cierre de caja",
                Content=$"Efectivo esperado: {Dinero(turno.EsperadoCentavos)}\n" +
                    $"Efectivo contado: {Dinero(contado)}\n" +
                    $"Diferencia: {Dinero(contado-turno.EsperadoCentavos)}\n" +
                    "El turno quedará cerrado para nuevos cobros.",
                PrimaryButtonText="Confirmar cierre",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            var arqueo=_db.CerrarTurno(turno.Id,contado);
            UltimoArqueoText.Text=$"Turno cerrado. Esperado {Dinero(arqueo.EsperadoCentavos)}" +
                $" · Contado {Dinero(arqueo.ContadoCentavos)} · Diferencia {Dinero(arqueo.DiferenciaCentavos)}.";
            Mensaje.Text="Turno cerrado y arqueo guardado.";
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="No se pudo cerrar caja: "+ex.Message; }
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
