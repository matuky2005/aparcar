using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Aparcar.Desktop;

public sealed partial class MainWindowView
{
    private IReadOnlyList<TalonarioFiscal> _talonariosFiscalesV25=Array.Empty<TalonarioFiscal>();

    private void ActualizarTarifasConfiguracion()
    {
        try
        {
            ConfigTarifaSectorCombo.Items.Clear();
            foreach (var sector in _db.ListarSectores())
                ConfigTarifaSectorCombo.Items.Add(new ComboBoxItem { Content = sector.Nombre, Tag = sector.Nombre });
            if (ConfigTarifaSectorCombo.Items.Count > 0 && ConfigTarifaSectorCombo.SelectedIndex < 0)
                ConfigTarifaSectorCombo.SelectedIndex = 0;
            MostrarTarifaConfiguracion();
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se cargaron las tarifas: " + ex.Message; }
    }

    private void ConfigTipoTarifa_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MostrarTarifaConfiguracion();

    private void ConfigTarifaSector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        MostrarTarifaSectorConfiguracion();

    private void MostrarTarifaConfiguracion()
    {
        if (_db is null || ConfigTipoTarifaCombo.SelectedItem is not ComboBoxItem item) return;
        try
        {
            var tipo = item.Content?.ToString() ?? "Auto";
            var tarifa = _db.ListarTarifas().Single(x => x.TipoVehiculo == tipo);
            ConfigTarifaVersionText.Text = $"{tipo}: versión {tarifa.Version} · vigente para próximos ingresos";
            ConfigTarifaPrimeraInput.Text = EditarImporte(tarifa.PrimeraHoraCentavos);
            ConfigTarifaFraccionInput.Text = EditarImporte(tarifa.FraccionCentavos);
            ConfigTarifaMinutosInput.Text = tarifa.MinutosFraccion.ToString(CultureInfo.InvariantCulture);
            ConfigTarifaTopeInput.Text = EditarImporte(tarifa.Tope24HorasCentavos);
            ConfigTarifaCortesiaInput.Text = tarifa.CortesiaMinutos.ToString(CultureInfo.InvariantCulture);
            MostrarTarifaSectorConfiguracion();
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se cargó la tarifa: " + ex.Message; }
    }

    private void MostrarTarifaSectorConfiguracion()
    {
        var sector = (ConfigTarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(sector)) return;
        try
        {
            var tipo = (ConfigTarifaSectorTipoCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
            var franja = (ConfigTarifaFranjaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? FranjasTarifarias.Diurna;
            var horario = _db.ObtenerHorarioTarifario(sector);
            ConfigHorarioInicioInput.Text = horario.InicioNoche.ToString("HH:mm");
            ConfigHorarioFinInput.Text = horario.FinNoche.ToString("HH:mm");
            var especifica = _db.ObtenerTarifaSector(sector, tipo, franja);
            var tarifa = especifica?.ComoTarifaOperativa() ?? _db.ListarTarifas().Single(x => x.TipoVehiculo == tipo);
            ConfigTarifaSectorVersionText.Text = especifica is null
                ? "Sin tarifa específica: se utilizará la general vigente al ingreso."
                : $"{sector}/{tipo}/{franja}: versión {especifica.Version}.";
            ConfigTarifaSectorPrimeraInput.Text = EditarImporte(tarifa.PrimeraHoraCentavos);
            ConfigTarifaSectorFraccionInput.Text = EditarImporte(tarifa.FraccionCentavos);
            ConfigTarifaSectorMinutosInput.Text = tarifa.MinutosFraccion.ToString(CultureInfo.InvariantCulture);
            ConfigTarifaSectorTopeInput.Text = EditarImporte(tarifa.Tope24HorasCentavos);
            ConfigTarifaSectorCortesiaInput.Text = tarifa.CortesiaMinutos.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se cargó la tarifa sectorial: " + ex.Message; }
    }

    private void ConfigGuardarTarifa_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tipo = (ConfigTipoTarifaCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (!int.TryParse(ConfigTarifaMinutosInput.Text, out var minutos) ||
                !int.TryParse(ConfigTarifaCortesiaInput.Text, out var cortesia))
                throw new ArgumentException("Minutos y cortesía deben ser números enteros.");
            var tarifa = _db.CambiarTarifa(tipo, LeerImporte(ConfigTarifaPrimeraInput.Text),
                LeerImporte(ConfigTarifaFraccionInput.Text), minutos,
                LeerImporte(ConfigTarifaTopeInput.Text), cortesia);
            ConfigTarifasEstadoText.Text = $"Tarifa general de {tipo} guardada. Nueva versión {tarifa.Version}.";
            MostrarTarifaConfiguracion();
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se guardó la tarifa general: " + ex.Message; }
    }

    private void ConfigGuardarHorario_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sector = (ConfigTarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new ArgumentException("Elegí un sector.");
            var horario = _db.ConfigurarHorarioTarifario(sector,
                LeerHorario(ConfigHorarioInicioInput.Text), LeerHorario(ConfigHorarioFinInput.Text));
            ConfigTarifasEstadoText.Text = $"Horario de {horario.Sector} guardado para futuros ingresos.";
            MostrarTarifaSectorConfiguracion();
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se guardó el horario: " + ex.Message; }
    }

    private void ConfigGuardarTarifaSector_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sector = (ConfigTarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new ArgumentException("Elegí un sector.");
            var tipo = (ConfigTarifaSectorTipoCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var franja = (ConfigTarifaFranjaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            if (!int.TryParse(ConfigTarifaSectorMinutosInput.Text, out var minutos) ||
                !int.TryParse(ConfigTarifaSectorCortesiaInput.Text, out var cortesia))
                throw new ArgumentException("Minutos y cortesía deben ser números enteros.");
            var tarifa = _db.GuardarTarifaSector(sector, tipo, franja,
                LeerImporte(ConfigTarifaSectorPrimeraInput.Text),
                LeerImporte(ConfigTarifaSectorFraccionInput.Text), minutos,
                LeerImporte(ConfigTarifaSectorTopeInput.Text), cortesia);
            ConfigTarifasEstadoText.Text = $"Tarifa {tarifa.Sector}/{tarifa.Franja} v{tarifa.Version} guardada.";
            MostrarTarifaSectorConfiguracion();
        }
        catch (Exception ex) { ConfigTarifasEstadoText.Text = "No se guardó la tarifa sectorial: " + ex.Message; }
    }

    private void InicioCoincidencias_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter || !InicioPrepararSalidaButton.IsEnabled)return;
        e.Handled=true;
        InicioPrepararSalida_Click(sender,new RoutedEventArgs());
    }

    private void ActualizarImpresoras_Click(object sender,RoutedEventArgs e) => CargarImpresoras();

    private void ProbarImpresora_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var nombre=ImpresorasCombo.SelectedItem?.ToString();
            if(string.IsNullOrWhiteSpace(nombre))
                throw new InvalidOperationException("Seleccioná una impresora instalada en Windows.");
            int mm=AnchoTicketCombo.SelectedIndex==0?58:80;
            TicketPrinter.ImprimirPrueba(nombre,mm);
            ImpresoraEstadoText.Text=$"Página de prueba enviada a {nombre} ({mm} mm). " +
                "Revisá si salió papel; si no, comprobá la cola y el controlador de Windows. " +
                "Guardá la selección para usarla en los tickets.";
        }
        catch(Exception ex)
        {
            ImpresoraEstadoText.Text="No se pudo enviar la página de prueba: "+ex.Message+
                ". Revisá la impresora, su conexión, la cola y el controlador de Windows.";
        }
    }

    private void TalonarioX_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_db is null || TalonariosXCombo.SelectedItem is not ComboBoxItem item)return;
        try
        {
            var t=_db.ListarTalonariosX().FirstOrDefault(x=>x.Codigo==item.Tag?.ToString());
            if(t is null)return;
            TalonarioXCodigoInput.Text=t.Codigo;
            TalonarioXNombreInput.Text=t.Nombre;
            TalonarioXHabilitadoCheck.IsChecked=t.Habilitado;
        }
        catch(Exception ex){TalonarioXResultadoText.Text="No se pudo consultar el talonario: "+ex.Message;}
    }

    private void TalonarioXGuardarCambios_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var codigo=TalonarioXSeleccionado();
            if(TalonarioXCodigoInput.Text.Trim()!=codigo)
                throw new ArgumentException("El código del talonario no se cambia. Para otro código, creá uno nuevo.");
            var t=_db.ActualizarTalonarioX(codigo,TalonarioXNombreInput.Text,
                TalonarioXHabilitadoCheck.IsChecked==true);
            ActualizarTalonariosX();
            TalonarioXResultadoText.Text=$"Talonario {t.Codigo} actualizado. Último número: {t.UltimoNumero:D8}.";
        }
        catch(Exception ex){TalonarioXResultadoText.Text="No se actualizó el talonario: "+ex.Message;}
    }

    private void FiscalMaestroActualizar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _talonariosFiscalesV25=_db.ListarTalonariosFiscales();
            FiscalMaestroList.ItemsSource=_talonariosFiscalesV25.Select(t=>
                $"{t.Ambiente} · {t.Cuit} · PV {t.PuntoVenta:D5} · tipo {t.Tipo} · {t.Nombre}"+
                (t.Habilitado?"":" · DESHABILITADO")).ToList();
            FiscalMaestroEstadoText.Text=$"{_talonariosFiscalesV25.Count} registros locales. " +
                "La habilitación real y la numeración se consultan en ARCA.";
        }
        catch(Exception ex){FiscalMaestroEstadoText.Text="No se pudo cargar el catálogo: "+ex.Message;}
    }

    private void FiscalMaestroList_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        var index=FiscalMaestroList.SelectedIndex;
        if(index<0 || index>=_talonariosFiscalesV25.Count)return;
        var t=_talonariosFiscalesV25[index];
        FiscalMaestroAmbienteCombo.SelectedIndex=t.Ambiente=="Homologacion"?0:1;
        FiscalMaestroCuitInput.Text=t.Cuit;
        FiscalMaestroPuntoInput.Text=t.PuntoVenta.ToString(CultureInfo.InvariantCulture);
        FiscalMaestroTipoCombo.SelectedIndex=t.Tipo==1?0:t.Tipo==6?1:2;
        FiscalMaestroNombreInput.Text=t.Nombre;
        FiscalMaestroHabilitadoCheck.IsChecked=t.Habilitado;
    }

    private void FiscalMaestroGuardar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!int.TryParse(FiscalMaestroPuntoInput.Text,out var punto) ||
               !int.TryParse((FiscalMaestroTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var tipo))
                throw new ArgumentException("Ingresá un punto de venta y elegí el tipo de factura.");
            var ambiente=(FiscalMaestroAmbienteCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
            _db.GuardarTalonarioFiscal(new TalonarioFiscal(ambiente,FiscalMaestroCuitInput.Text.Trim(),
                punto,tipo,FiscalMaestroNombreInput.Text,FiscalMaestroHabilitadoCheck.IsChecked==true));
            FiscalMaestroActualizar_Click(sender,e);
            FiscalMaestroEstadoText.Text="Punto de venta guardado localmente. " +
                "Si cambiás ambiente, CUIT, punto o clase se crea otro registro.";
        }
        catch(Exception ex){FiscalMaestroEstadoText.Text="No se guardó: "+ex.Message;}
    }

    private void FiscalMaestroAplicar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var i=FiscalMaestroList.SelectedIndex;
            if(i<0 || i>=_talonariosFiscalesV25.Count)throw new ArgumentException("Elegí un registro del listado.");
            var t=_talonariosFiscalesV25[i];
            if(!t.Habilitado || t.Ambiente!="Homologacion")
                throw new InvalidOperationException("Solo podés usar aquí un talonario habilitado de homologación. Producción aún no está habilitada.");
            ArcaCuitInput.Text=t.Cuit;
            ArcaPtoVtaInput.Text=t.PuntoVenta.ToString(CultureInfo.InvariantCulture);
            ArcaCbteTipoCombo.SelectedIndex=t.Tipo==1?0:t.Tipo==6?1:2;
            ConfigSectionNavigation.SelectedIndex=5;
            ArcaResultadoText.Text="Datos del punto de venta cargados. Seleccioná su certificado de homologación y consultá ARCA.";
        }
        catch(Exception ex){FiscalMaestroEstadoText.Text="No se aplicó: "+ex.Message;}
    }

    private void ConfigAbrirLavaderoMaestros_Click(object sender, RoutedEventArgs e)
    {
        Navegacion.SelectedItem = Navegacion.MenuItems[6];
        DispatcherQueue.TryEnqueue(() => LavaderoPanelTabs.SelectedIndex = 1);
    }

    private void ConfigAbrirAbonadosMaestros_Click(object sender, RoutedEventArgs e)
    {
        Navegacion.SelectedItem = Navegacion.MenuItems[7];
        DispatcherQueue.TryEnqueue(() => AbonadosPanelTabs.SelectedIndex = 1);
    }

    private void ConfigAbrirUsuarios_Click(object sender, RoutedEventArgs e)
    {
        Navegacion.SelectedItem = Navegacion.MenuItems[10];
    }

    private void ConfigAbrirAuditoria_Click(object sender, RoutedEventArgs e)
    {
        Navegacion.SelectedItem = Navegacion.MenuItems[11];
    }

    private static string CarpetaCertificados(string ambiente)
    {
        if(ambiente is not ("Homologacion" or "Produccion"))throw new ArgumentException("Elegí el ambiente.");
        var documentos=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(string.IsNullOrWhiteSpace(documentos))throw new InvalidOperationException("No se encontró la carpeta Documentos de Windows.");
        var carpeta=Path.Combine(documentos,"Aparcar","ARCA",ambiente);
        Directory.CreateDirectory(carpeta);
        return carpeta;
    }

    private string AmbienteCertificado() =>
        (CertAmbienteCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";

    private static void GuardarNuevoArchivo(string ruta,byte[] contenido)
    {
        using var archivo=new FileStream(ruta,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        archivo.Write(contenido,0,contenido.Length);
        archivo.Flush(true);
    }

    private void CertGenerarSolicitud_Click(object sender,RoutedEventArgs e)
    {
        string? claveRuta=null;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var ambiente=AmbienteCertificado();
            var solicitud=CertificadosArcaV25.CrearSolicitud(CertCuitInput.Text,
                CertOrganizacionInput.Text,CertNombreInput.Text,CertClaveInput.Password);
            var carpeta=CarpetaCertificados(ambiente);
            var nombre="Aparcar-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
            claveRuta=Path.Combine(carpeta,nombre+"-clave.pem");
            var csrRuta=Path.Combine(carpeta,nombre+".csr");
            GuardarNuevoArchivo(claveRuta,System.Text.Encoding.UTF8.GetBytes(solicitud.ClavePrivadaProtegidaPem));
            GuardarNuevoArchivo(csrRuta,System.Text.Encoding.UTF8.GetBytes(solicitud.CsrPem));
            CertClaveRutaInput.Text=claveRuta;
            CertEstadoText.Text=$"Solicitud creada para {ambiente}.\nCSR para presentar en ARCA: {csrRuta}\n"+
                $"Clave privada cifrada: {claveRuta}\nConservá la clave y su contraseña. " +
                "ARCA debe emitir el certificado y asociarlo a los servicios correspondientes.";
        }
        catch(Exception ex)
        {
            CertEstadoText.Text="No se completó la solicitud: "+ex.Message+
                (claveRuta is not null && File.Exists(claveRuta)?". Se conservó la clave protegida en: "+claveRuta:"");
        }
        finally {CertClaveInput.Password="";}
    }

    private async System.Threading.Tasks.Task<string?> ElegirArchivo(params string[] extensiones)
    {
        var selector=new FileOpenPicker{SuggestedStartLocation=PickerLocationId.DocumentsLibrary};
        foreach(var ext in extensiones)selector.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(selector,WindowNative.GetWindowHandle(_hostWindow));
        var archivo=await selector.PickSingleFileAsync();
        return archivo?.Path;
    }

    private async void CertExaminarClave_Click(object sender,RoutedEventArgs e)
    {
        try {var ruta=await ElegirArchivo(".pem",".key");if(ruta is not null)CertClaveRutaInput.Text=ruta;}
        catch(Exception ex){CertEstadoText.Text="No se pudo elegir la clave: "+ex.Message;}
    }
    private async void CertExaminarFirmado_Click(object sender,RoutedEventArgs e)
    {
        try {var ruta=await ElegirArchivo(".crt",".cer",".pem");if(ruta is not null)CertFirmadoRutaInput.Text=ruta;}
        catch(Exception ex){CertEstadoText.Text="No se pudo elegir el certificado: "+ex.Message;}
    }
    private async void ArcaExaminarPfx_Click(object sender,RoutedEventArgs e)
    {
        try {var ruta=await ElegirArchivo(".pfx",".p12");if(ruta is not null)ArcaCertificadoInput.Text=ruta;}
        catch(Exception ex){ArcaResultadoText.Text="No se pudo elegir el PFX: "+ex.Message;}
    }

    private void CertCrearPfx_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var ambiente=AmbienteCertificado();
            var certificado=File.ReadAllBytes(CertFirmadoRutaInput.Text.Trim());
            var clave=File.ReadAllText(CertClaveRutaInput.Text.Trim());
            var pfx=CertificadosArcaV25.CrearPfx(certificado,clave,CertClaveInput.Password,CertPfxClaveInput.Password);
            var ruta=Path.Combine(CarpetaCertificados(ambiente),
                "Aparcar-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".pfx");
            GuardarNuevoArchivo(ruta,pfx);
            if(ambiente=="Homologacion")ArcaCertificadoInput.Text=ruta;
            CertEstadoText.Text=$"PFX protegido creado en {ruta}. Verificá que ARCA haya emitido el certificado para {ambiente} " +
                "y que esté asociado a los servicios. La contraseña no se guardó.";
        }
        catch(Exception ex){CertEstadoText.Text="No se creó el PFX: "+ex.Message;}
        finally {CertClaveInput.Password="";CertPfxClaveInput.Password="";}
    }
}
