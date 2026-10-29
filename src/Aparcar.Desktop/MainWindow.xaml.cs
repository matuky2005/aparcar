using System;
using System.IO;
using System.Drawing.Printing;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Aparcar.Desktop;

public sealed partial class MainWindowView : UserControl
{
    private readonly Window _hostWindow;
    private readonly AparcarDb _db;
    private Estadia? _estadiaSeleccionada;
    private bool _guardandoIngreso;
    private bool _procesandoSalida;
    private bool _procesandoImpresionCobro;
    private string? _impresora;
    private int _anchoTicket = 80;
    private bool _autoImprimirIngreso = true;
    private bool _autoImprimirCobro = true;
    private string? _respaldoInicioRuta;
    private CotizacionOperativa? _cotizacion;
    private Guid _claveCobro = Guid.Empty;
    private ResumenRecaudacion? _reporteActual;
    private System.Collections.Generic.List<UsuarioListado> _usuariosActuales = new();
    private System.Collections.Generic.IReadOnlyList<Estadia> _coincidenciasRapidasV22=Array.Empty<Estadia>();
    private CotizacionOperativa? _cotizacionInicioV23;
    private Guid _claveInicioV23=Guid.Empty;
    private bool _procesandoInicioV23;
    private System.Collections.Generic.List<ServicioLavado> _serviciosLavado=new();
    private System.Collections.Generic.List<OrdenLavado> _ordenesLavado=new();
    private System.Collections.Generic.List<PlanAbono> _planesAbono=new();
    private System.Collections.Generic.List<ContratoAbono> _contratosAbono=new();
    private bool _procesandoCobroExtra;
    private System.Collections.Generic.List<SectorEstacionamiento> _sectoresConfigurados = new();

    public MainWindowView(Window hostWindow)
    {
        _hostWindow = hostWindow ?? throw new ArgumentNullException(nameof(hostWindow));
        InitializeComponent();
        ConfigSectionNavigation.SelectedIndex = 0;

        // V0.1: una sola cuenta de Windows. Fuera del directorio del paquete MSIX.
        var ruta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Aparcar", "Datos", "aparcar.db");
        string? avisoRestauracion=null;
        try{avisoRestauracion=AparcarDb.AplicarRestauracionPendiente(ruta);}
        catch(Exception ex){avisoRestauracion="Recuperación no aplicada: "+ex.Message;}
        _db = new AparcarDb(ruta);
        _db.Inicializar();

        // El respaldo de arranque se realiza antes de mostrar la aplicación.
        // Si falla, el programa sigue disponible y deja el motivo visible.
        try
        {
            var respaldoAutomatico = _db.ObtenerPreferenciaInicial("RespaldoAutomaticoInicio") ?? "true";
            _respaldoInicioRuta = respaldoAutomatico == "true"
                ? _db.CrearRespaldoAutomatico()
                : "Respaldo automático de inicio desactivado.";
        }
        catch (Exception ex) { _respaldoInicioRuta = "No se pudo crear el respaldo automático: " + ex.Message; }

        RutaDatosText.Text = _db.RutaDb;
        if(avisoRestauracion is not null)
            EstadoRestauracionText.Text="Resultado de recuperación: "+avisoRestauracion;
        Navegacion.Visibility=Visibility.Collapsed;
        PantallaAcceso.Visibility=Visibility.Visible;
        ReporteDesdeInput.Text=DateTime.Today.ToString("dd/MM/yyyy");
        ReporteHastaInput.Text=DateTime.Today.ToString("dd/MM/yyyy");
        MostrarAcceso();
        // Expiración de la sesión también cuando el operador no toca la interfaz.
        var reloj=DispatcherQueue.CreateTimer();
        reloj.Interval=TimeSpan.FromSeconds(20);
        reloj.IsRepeating=true;
        reloj.Tick+=(_,_)=>{
            if(Navegacion.Visibility==Visibility.Visible && _db.SesionActual is null)
                BloquearInterfaz();
        };
        reloj.Start();
        SplashEstadoText.Text = _respaldoInicioRuta is not null &&
            _respaldoInicioRuta.StartsWith("No se pudo", StringComparison.Ordinal)
            ? _respaldoInicioRuta
            : "Base de datos y respaldo de seguridad listos.";
        // La inicialización ocurre en el constructor; diferimos el cierre para que
        // WinUI tenga tiempo de pintar el splash antes de mostrar el acceso.
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(850);
            SplashOverlay.Visibility = Visibility.Collapsed;
        });
    }

    private void MostrarAcceso()
    {
        bool necesitaAdmin=!_db.HayAdministrador();
        PrimerAdministradorPanel.Visibility=necesitaAdmin?Visibility.Visible:Visibility.Collapsed;
        LoginPanel.Visibility=necesitaAdmin?Visibility.Collapsed:Visibility.Visible;
        AccesoMensaje.Text="";
        LoginPasswordBox.Password="";
        AdminInicialPasswordBox.Password="";
        ArcaClavePfxInput.Password="";
        CertClaveInput.Password="";
        CertPfxClaveInput.Password="";
        PantallaAcceso.Visibility=Visibility.Visible;
        Navegacion.Visibility=Visibility.Collapsed;
        SalirButton.Visibility=Visibility.Collapsed;
        UsuarioActivoTexto.Text="Sin sesión";
        LimpiarBusquedaRapidaV22();
    }

    private void BloquearInterfaz()
    {
        _db.CerrarSesion();
        MostrarAcceso();
    }

    private void DesbloquearInterfaz(UsuarioSesion sesion)
    {
        PantallaAcceso.Visibility=Visibility.Collapsed;
        Navegacion.Visibility=Visibility.Visible;
        SalirButton.Visibility=Visibility.Visible;
        UsuarioActivoTexto.Text=$"{sesion.Usuario} · {sesion.Rol}";
        var rol=sesion.Rol;
        MenuUsuarios.Visibility=RolesAparcar.Permite(rol,PermisosAparcar.Usuarios)
            ?Visibility.Visible:Visibility.Collapsed;
        MenuAuditoria.Visibility=RolesAparcar.Permite(rol,PermisosAparcar.Auditoria)
            ?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[1]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.Ingreso)?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[2]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.Cobro)?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[4]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.Tarifas)?Visibility.Visible:Visibility.Collapsed;
        MenuTarifas.Visibility=Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[5]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.AbrirCaja)?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[9]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.Configuracion)?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[6]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.LavaderoConsultar)?Visibility.Visible:Visibility.Collapsed;
        ((NavigationViewItem)Navegacion.MenuItems[7]).Visibility=
            RolesAparcar.Permite(rol,PermisosAparcar.AbonosConsultar)?Visibility.Visible:Visibility.Collapsed;
        LavGuardarServicioButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.LavaderoCatalogo);
        LavCrearOrdenButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.LavaderoOperar);
        LavCambiarEstadoButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.LavaderoOperar);
        LavCobrarButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Cobro);
        AbGuardarPlanButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.AbonosAdministrar);
        AbCrearContratoButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.AbonosAdministrar);
        AbCobrarButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Cobro);
        FacturarLavadoButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        FacturarLavadoCfButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        FacturarAbonoButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        FacturarAbonoCfButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        CargarImpresoras();
        var autoAnterior=_db.ObtenerPreferencia("AutoImprimir")!="false";
        _autoImprimirIngreso=(_db.ObtenerPreferencia("AutoImprimirIngreso")??(autoAnterior?"true":"false"))=="true";
        _autoImprimirCobro=(_db.ObtenerPreferencia("AutoImprimirCobro")??(autoAnterior?"true":"false"))=="true";
        AutoImprimirIngresoCheck.IsChecked=_autoImprimirIngreso;
        AutoImprimirCobroCheck.IsChecked=_autoImprimirCobro;
        TipoTarifaCombo.SelectedIndex=0;
        ActualizarSectores();
        ActualizarTarifasSector();
        GuardarTarifaSectorButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        ConfigGuardarTarifaButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        ConfigGuardarHorarioButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        ConfigGuardarTarifaSectorButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        GuardarHorarioButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        var puedeConfigurar=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        ConfigGuardarSectorButton.IsEnabled=puedeConfigurar;
        ConfigGuardarPlazaButton.IsEnabled=puedeConfigurar;
        GuardarPoliticaCajaButton.IsEnabled=puedeConfigurar;
        GuardarRespaldoPoliticaButton.IsEnabled=puedeConfigurar;
        ArcaProbarConexionButton.IsEnabled=puedeConfigurar;
        FiscalConsultarCondicionesButton.IsEnabled=puedeConfigurar;
        FiscalPrepararButton.IsEnabled=puedeConfigurar;
        FiscalEmitirButton.IsEnabled=puedeConfigurar;
        FiscalPrepararNotaCreditoButton.IsEnabled=puedeConfigurar;
        FiscalEmitirNotaCreditoButton.IsEnabled=puedeConfigurar;
        FiscalConciliarNotaCreditoButton.IsEnabled=puedeConfigurar;
        FiscalConciliarButton.IsEnabled=puedeConfigurar;
        FiscalGuardarPdfButton.IsEnabled=puedeConfigurar;
        FiscalImprimirButton.IsEnabled=puedeConfigurar;
        TalonarioXCrearButton.IsEnabled=puedeConfigurar;
        TalonarioXGuardarCambiosButton.IsEnabled=puedeConfigurar;
        TalonarioXGuardarPreferidoButton.IsEnabled=puedeConfigurar;
        TalonarioXActualizarButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Consulta);
        TalonarioXEmitirButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Cobro);
        TalonarioXReimprimirButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Reimpresion);
        ControlDocumentalActualizarButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Reportes);
        FiscalGuardarDatosDocumentoButton.IsEnabled=puedeConfigurar;
        FiscalBuscarClientesButton.IsEnabled=puedeConfigurar;
        FiscalAplicarClienteButton.IsEnabled=puedeConfigurar;
        FiscalGuardarClienteButton.IsEnabled=puedeConfigurar;
        FiscalVersionesClienteButton.IsEnabled=puedeConfigurar;
        FiscalDesactivarClienteButton.IsEnabled=puedeConfigurar;
        FiscalConsultarConstanciaButton.IsEnabled=puedeConfigurar;
        FiscalConsultarConstanciaEmisorButton.IsEnabled=puedeConfigurar;
        FiscalGuardarPreferenciaCfButton.IsEnabled=puedeConfigurar;
        FiscalAplicarConsumidorFinalButton.IsEnabled=puedeConfigurar;
        FiscalCfPorDefectoCheck.IsEnabled=puedeConfigurar;
        FiscalPerfilActualizarButton.IsEnabled=puedeConfigurar;
        FiscalPerfilAplicarButton.IsEnabled=puedeConfigurar;
        FiscalPerfilGuardarButton.IsEnabled=puedeConfigurar;
        FiscalPerfilHistorialButton.IsEnabled=puedeConfigurar;
        CajaCiegaObligatoriaCheck.IsEnabled=puedeConfigurar;
        CajaCiegaObligatoriaCheck.IsChecked=_db.CajaCiegaObligatoria();
        RespaldoAutomaticoInicioCheck.IsChecked=
            (_db.ObtenerPreferencia("RespaldoAutomaticoInicio") ?? "true") == "true";
        RespaldoEstadoText.Text = _respaldoInicioRuta is null
            ? "No se generó respaldo de arranque."
            : _respaldoInicioRuta.StartsWith("No se pudo", StringComparison.Ordinal)
                ? _respaldoInicioRuta
                : "Respaldo de arranque: " + _respaldoInicioRuta;
        if(_db.CajaCiegaObligatoria())
        {
            CajaCiegaTurnoCheck.IsChecked=true;
            CajaCiegaTurnoCheck.IsEnabled=false;
        }
        ReimprimirTicketButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Reimpresion);
        ReciboCobroActualizarButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Consulta);
        FacturarCobroButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        FacturarConsumidorFinalButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        ReciboCobroImprimirOriginalButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Impresion);
        ReciboCobroImprimirCopiaButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Reimpresion);
        InicioNuevoIngresoButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Ingreso);
        InicioIrSalidaButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Cobro);
        InicioPrepararSalidaButton.IsEnabled=false;
        InicioEstimarSalidaButton.IsEnabled=false;
        CancelarPreparacionInicioV23();
        PrepararRestauracionButton.IsEnabled=
            RolesAparcar.Permite(rol,PermisosAparcar.Restauracion);
        Navegacion.SelectedItem=Navegacion.MenuItems[0];
        Mostrar("inicio");
    }

    private void CrearAdministradorInicial_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var nombre=AdminInicialUsuarioInput.Text.Trim();
            var contrasena=AdminInicialPasswordBox.Password;
            _db.CrearAdministradorInicial(nombre,contrasena);
            var sesion=_db.IniciarSesion(nombre,contrasena);
            AdminInicialPasswordBox.Password="";
            DesbloquearInterfaz(sesion);
            Mensaje.Text="Administrador inicial creado. Configurá los demás usuarios en Seguridad.";
        }
        catch(Exception ex)
        {
            AdminInicialPasswordBox.Password="";
            AccesoMensaje.Text=ex.Message;
        }
    }

    private void Login_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var sesion=_db.IniciarSesion(LoginUsuarioInput.Text,LoginPasswordBox.Password);
            LoginPasswordBox.Password="";
            DesbloquearInterfaz(sesion);
            Mensaje.Text=$"Sesión iniciada por {sesion.Usuario}.";
        }
        catch(Exception ex)
        {
            LoginPasswordBox.Password="";
            AccesoMensaje.Text=ex.Message;
        }
    }

    private void CerrarSesion_Click(object sender,RoutedEventArgs e)
    {
        try { _db.CerrarSesion(); }
        finally { MostrarAcceso(); }
    }

    private void CrearUsuario_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Usuarios);
            var rol=(NuevoRolCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";
            var creado=_db.CrearUsuario(NuevoUsuarioInput.Text,
                NuevaContrasenaBox.Password,rol);
            NuevaContrasenaBox.Password="";
            NuevoUsuarioInput.Text="";
            Mensaje.Text=$"Usuario {creado.Nombre} ({creado.Rol}) creado.";
            RefrescarUsuarios();
        }
        catch(Exception ex)
        {
            NuevaContrasenaBox.Password="";
            Mensaje.Text="No se pudo crear el usuario: "+ex.Message;
        }
    }

    private void RefrescarUsuarios()
    {
        try
        {
            _usuariosActuales=_db.ListarUsuarios();
            ListaUsuarios.ItemsSource=_usuariosActuales
                .Select(u=>$"{u.Nombre} · {u.Rol} · {(u.Activo?"Activo":"Deshabilitado")}").ToList();
        }
        catch(Exception ex){Mensaje.Text="Usuarios: "+ex.Message;}
    }

    private UsuarioListado SeleccionUsuario()
    {
        _db.ExigirPermiso(PermisosAparcar.Usuarios);
        var index=ListaUsuarios.SelectedIndex;
        if(index<0 || index>=_usuariosActuales.Count)
            throw new ArgumentException("Seleccioná una cuenta de la lista.");
        return _usuariosActuales[index];
    }

    private void CambiarRolUsuario_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var usuario=SeleccionUsuario();
            var rol=(RolCambioCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";
            _db.CambiarRolUsuario(usuario.Id,rol);
            Mensaje.Text=$"Rol de {usuario.Nombre} actualizado a {rol}.";
            RefrescarUsuarios();
        }
        catch(Exception ex){ Mensaje.Text="No se cambió el rol: "+ex.Message; }
    }

    private async void DesactivarUsuario_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var usuario=SeleccionUsuario();
            var confirmacion=new ContentDialog
            {
                Title="Desactivar usuario",
                Content=$"Se bloqueará el acceso de {usuario.Nombre}. ¿Confirmás?",
                PrimaryButtonText="Desactivar",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await confirmacion.ShowAsync()!=ContentDialogResult.Primary)return;
            _db.DesactivarUsuario(usuario.Id);
            Mensaje.Text=$"Usuario {usuario.Nombre} desactivado.";
            RefrescarUsuarios();
        }
        catch(Exception ex){ Mensaje.Text="No se desactivó: "+ex.Message; }
    }

    private void RestablecerUsuario_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var usuario=SeleccionUsuario();
            _db.RestablecerContrasenaUsuario(usuario.Id,ResetPasswordBox.Password);
            ResetPasswordBox.Password="";
            Mensaje.Text=$"Contraseña restablecida de {usuario.Nombre}.";
        }
        catch(Exception ex)
        {
            ResetPasswordBox.Password="";
            Mensaje.Text="No se restableció contraseña: "+ex.Message;
        }
    }

    private void CargarAuditoria()
    {
        try
        {
            ListaAuditoria.ItemsSource=_db.ListarAuditoria(200)
                .Select(a=>$"#{a.Secuencia} {a.FechaUtc.ToLocalTime():dd/MM HH:mm:ss} · {a.Actor} · {a.Accion} · {a.Detalle}")
                .ToList();
        }
        catch(Exception ex){Mensaje.Text="Auditoría: "+ex.Message;}
    }

    private void VerificarAuditoria_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var ok=_db.VerificarCadenaAuditoria();
            AuditoriaEstadoText.Text=ok?"Cadena de auditoría consistente.":"ATENCIÓN: cadena de auditoría alterada o incompleta.";
            CargarAuditoria();
        }
        catch(Exception ex){MensagearSeguridad(ex);}
    }

    private void MensagearSeguridad(Exception ex) =>
        Mensaje.Text="Error de seguridad: "+ex.Message;

    private static string Dinero(long centavos) =>
        (centavos / 100m).ToString("C2",
            System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

    private void Navegacion_SelectionChanged(
        NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string pantalla)
            Mostrar(pantalla);
    }

    private void ConfigSection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var pantalla = (ConfigSectionNavigation.SelectedItem as ListViewItem)?.Tag?.ToString() ?? "tarifas";
        ConfigSectionTarifas.Visibility = pantalla == "tarifas" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionMaestros.Visibility = pantalla == "maestros" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionSectores.Visibility = pantalla == "sectores" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionImpresion.Visibility = pantalla == "impresion" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionTalonarios.Visibility = pantalla == "talonarios" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionArca.Visibility = pantalla == "arca" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionFiscal.Visibility = pantalla == "fiscal" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionSolicitudes.Visibility = pantalla == "solicitudes" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionDocumental.Visibility = pantalla == "documental" ? Visibility.Visible : Visibility.Collapsed;
        ConfigSectionSeguridad.Visibility = pantalla == "seguridad" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Mostrar(string pantalla)
    {
        if(_db is null)return;
        if(_db.SesionActual is null)
        {
            if(Navegacion.Visibility==Visibility.Visible)BloquearInterfaz();
            return;
        }
        UsuariosPanel.Visibility=pantalla=="usuarios"?Visibility.Visible:Visibility.Collapsed;
        AuditoriaPanel.Visibility=pantalla=="auditoria"?Visibility.Visible:Visibility.Collapsed;
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
        if (pantalla == "ingreso")
        {
            ActualizarSectores();
            DispatcherQueue.TryEnqueue(() => { if(IngresoPanel.Visibility==Visibility.Visible) PatenteInput.Focus(FocusState.Programmatic); });
        }
        if (pantalla == "salida")
        {
            if(RolesAparcar.Permite(_db.SesionActual.Rol,PermisosAparcar.Consulta))
                ActualizarRecibosCobroV21();
            SalidaPanelTabs.SelectedItem=SalidaPanelTabs.TabItems[0];
            DispatcherQueue.TryEnqueue(() => { if(SalidaPanel.Visibility==Visibility.Visible) BusquedaInput.Focus(FocusState.Programmatic); });
        }
        if (pantalla == "caja") { ActualizarCaja(); ActualizarCierresZ(); }
        if (pantalla == "tarifas"){MostrarTarifaSeleccionada();ActualizarTarifasSector();}
        if (pantalla == "lavadero") RefrescarLavadero();
        if (pantalla == "abonados") RefrescarAbonados();
        if (pantalla == "reportes") ConsultarReporteActual();
        if (pantalla == "config") { ActualizarSectores(); TalonarioXActualizar_Click(this,new RoutedEventArgs()); FiscalMaestroActualizar_Click(this,new RoutedEventArgs()); }
        if (pantalla == "config") ActualizarTarifasConfiguracion();
        if (pantalla == "usuarios") RefrescarUsuarios();
        if (pantalla == "auditoria") CargarAuditoria();
    }

    private void IrIngreso_Click(object sender, RoutedEventArgs e) =>
        Navegacion.SelectedItem = Navegacion.MenuItems[1];

    private void IrSalida_Click(object sender, RoutedEventArgs e) =>
        Navegacion.SelectedItem = Navegacion.MenuItems[2];

    private void Actualizar_Click(object sender, RoutedEventArgs e) => Refrescar();

    private void Refrescar()
    {
        var presentes=_db.ListarActivas();
        TotalActivosText.Text=presentes.Count.ToString(CultureInfo.InvariantCulture);
        var filas=presentes
            .Select(x=>$"#{x.Ticket:D7}   {x.Patente,-12}  {x.TipoVehiculo,-11}  {x.IngresoUtc.ToLocalTime():dd/MM HH:mm}")
            .ToList();
        PresentesList.ItemsSource=filas;
        if(InicioPanel.Visibility==Visibility.Visible)
            ActualizarPanelOperativoV22();
    }

    private void ActualizarPanelOperativoV22()
    {
        try
        {
            var panel=_db.ObtenerPanelOperativoV22(12);
            TotalActivosText.Text=panel.Presentes.ToString(CultureInfo.InvariantCulture);
            InicioLibresText.Text=panel.LibresHabilitados.ToString(CultureInfo.InvariantCulture);
            InicioOcupacionText.Text=
                $"Capacidad de sectores habilitados: {panel.CapacidadHabilitada:N0} · "+
                $"Disponibles efectivos: {panel.LibresHabilitados:N0}. "+
                "La disponibilidad en sectores con plaza asignada requiere plazas habilitadas libres.";
            InicioCajaEstadoText.Text=panel.CajaAbierta
                ?$"Turno de caja ABIERTO · Operador: {panel.OperadorTurno}."
                :"Turno de caja CERRADO · Para cobrar debés abrir uno.";
            InicioSectoresList.ItemsSource=panel.Sectores.Select(sec=>
                $"{sec.Nombre} · {(sec.Activo?"HABILITADO":"SUSPENDIDO")} · "+
                $"{sec.Ocupados}/{sec.Capacidad} ocupados · {sec.Libres} disponibles · "+
                (sec.Modo==ModosSector.Asignado?"plaza asignada":"sector libre")).ToList();
            ResumenActivosList.ItemsSource=panel.UltimosIngresos.Select(e=>
                $"#{e.Ticket:D7} · {e.Patente} · {e.TipoVehiculo} · {e.Sector} · "+
                $"{e.IngresoUtc.ToLocalTime():dd/MM HH:mm}").ToList();
            InicioUltimasSalidasList.ItemsSource=panel.UltimasSalidas.Select(c=>
                $"#{c.Ticket:D7} · {c.Patente} · {c.MedioPago} · "+
                $"{c.FechaUtc.ToLocalTime():dd/MM HH:mm}"+(c.Devuelto?" · DEVUELTO":"")).ToList();
            InicioPanelEstadoText.Text=
                $"Actualizado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}. "+
                "La estimación de tarifa no congela el precio: se vuelve a calcular al cobrar. "+
                "Las salidas listadas tienen cobro confirmado, sin representar su estado fiscal.";
        }
        catch(Exception ex)
        {
            TotalActivosText.Text="—";
            InicioLibresText.Text="—";
            InicioCajaEstadoText.Text="Estado de caja no disponible.";
            InicioOcupacionText.Text="";
            InicioSectoresList.ItemsSource=null;
            ResumenActivosList.ItemsSource=null;
            InicioUltimasSalidasList.ItemsSource=null;
            InicioPanelEstadoText.Text="Error al consultar datos del panel: "+ex.Message+
                ". No se modificó ninguna operación.";
        }
    }

    private void LimpiarBusquedaRapidaV22()
    {
        CancelarPreparacionInicioV23();
        _coincidenciasRapidasV22=Array.Empty<Estadia>();
        InicioCoincidenciasList.ItemsSource=null;
        InicioCoincidenciasList.Visibility=Visibility.Collapsed;
        InicioOpcionesBusquedaPanel.Visibility=Visibility.Collapsed;
        InicioBusquedaInput.Text="";
        InicioBusquedaEstadoText.Text="";
        InicioEstimacionText.Text="";
        InicioEstimarSalidaButton.IsEnabled=false;
        InicioPrepararSalidaButton.IsEnabled=false;
    }

    private void CancelarPreparacionInicioV23()
    {
        _cotizacionInicioV23=null;
        _claveInicioV23=Guid.Empty;
        InicioCobroPanelV23.Visibility=Visibility.Collapsed;
        InicioConfirmarCobroButton.IsEnabled=false;
        InicioCobroEmitirXCheck.IsChecked=false;
        InicioCobroReferenciaInput.Text="";
        InicioCobroRecibidoInput.Text="";
        InicioCobroVerificadoCheck.IsChecked=false;
        InicioCobroVueltoText.Text="";
        InicioCobroEstadoText.Text="";
        InicioCobroDetalleText.Text="";
        InicioCobroImporteText.Text="";
        InicioCobroVigenciaText.Text="";
    }

    private void InicioBusqueda_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        InicioBuscar_Click(sender,new RoutedEventArgs());
    }

    private void InicioBuscar_Click(object sender,RoutedEventArgs e)
    {
        CancelarPreparacionInicioV23();
        _coincidenciasRapidasV22=Array.Empty<Estadia>();
        InicioCoincidenciasList.ItemsSource=null;
        InicioCoincidenciasList.Visibility=Visibility.Collapsed;
        InicioOpcionesBusquedaPanel.Visibility=Visibility.Collapsed;
        InicioEstimacionText.Text="";
        InicioEstimarSalidaButton.IsEnabled=false;
        InicioPrepararSalidaButton.IsEnabled=false;
        try
        {
            var resultados=_db.BuscarPresentesRapidoV22(InicioBusquedaInput.Text,25);
            _coincidenciasRapidasV22=resultados;
            InicioCoincidenciasList.Visibility=resultados.Count>1?Visibility.Visible:Visibility.Collapsed;
            InicioOpcionesBusquedaPanel.Visibility=resultados.Count>1?Visibility.Visible:Visibility.Collapsed;
            InicioCoincidenciasList.ItemsSource=resultados.Select(estadia=>
                $"#{estadia.Ticket:D7} · {estadia.Patente} · {estadia.TipoVehiculo} · "+
                $"{estadia.Sector} · {estadia.IngresoUtc.ToLocalTime():dd/MM HH:mm}").ToList();
            if(resultados.Count==1)
                InicioCoincidenciasList.SelectedIndex=0;
            InicioBusquedaEstadoText.Text=resultados.Count==0
                ?"No hay vehículos presentes con ese ticket o prefijo de patente."
                :$"{resultados.Count} coincidencia(s)"+
                    (resultados.Count==25?" (máximo mostrado; afiná la búsqueda)":"")+
                    ". Elegí el vehículo antes de preparar la salida.";
            if(resultados.Count==1 && InicioPrepararSalidaButton.IsEnabled)
                InicioPrepararSalida_Click(sender,new RoutedEventArgs());
            else if(resultados.Count>1)
                DispatcherQueue.TryEnqueue(() =>
                {if(InicioPanel.Visibility==Visibility.Visible && InicioCoincidenciasList.Visibility==Visibility.Visible)
                    InicioCoincidenciasList.Focus(FocusState.Programmatic);});
        }
        catch(Exception ex)
        {
            InicioBusquedaEstadoText.Text="Búsqueda no realizada: "+ex.Message;
        }
    }

    private Estadia EstadiaRapidaSeleccionadaV22()
    {
        var indice=InicioCoincidenciasList.SelectedIndex;
        if(indice<0 || indice>=_coincidenciasRapidasV22.Count)
            throw new ArgumentException("Elegí un vehículo de los resultados.");
        return _coincidenciasRapidasV22[indice];
    }

    private void InicioCoincidencias_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        CancelarPreparacionInicioV23();
        var hay=InicioCoincidenciasList.SelectedIndex>=0 &&
            InicioCoincidenciasList.SelectedIndex<_coincidenciasRapidasV22.Count;
        var rol=_db?.SesionActual?.Rol??"";
        InicioEstimarSalidaButton.IsEnabled=hay && RolesAparcar.Permite(rol,PermisosAparcar.Consulta);
        InicioPrepararSalidaButton.IsEnabled=hay && RolesAparcar.Permite(rol,PermisosAparcar.Cobro);
        InicioEstimacionText.Text="";
    }

    private void InicioEstimarSalida_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var elegido=EstadiaRapidaSeleccionadaV22();
            var actual=_db.BuscarActiva(elegido.Ticket.ToString(CultureInfo.InvariantCulture));
            if(actual is null || actual.Id!=elegido.Id)
                throw new InvalidOperationException("La estadía cambió. Actualizá la búsqueda.");
            var calculo=_db.CotizarSalida(elegido.Id);
            InicioEstimacionText.Text=
                $"Estimación para #{elegido.Ticket:D7} · {elegido.Patente}: "+
                Dinero(calculo.ImporteCentavos)+(calculo.CubiertoPorAbono?" · CUBIERTO POR ABONO":"")+
                $". Cotización válida hasta {calculo.VenceUtc.ToLocalTime():HH:mm:ss}. "+
                "Se recalcula al confirmar cobro; esta consulta NO cobra ni finaliza.";
        }
        catch(Exception ex)
        {
            InicioEstimacionText.Text="No se pudo estimar: "+ex.Message;
        }
    }

    private void InicioPrepararSalida_Click(object sender,RoutedEventArgs e)
    {
        CancelarPreparacionInicioV23();
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Cobro);
            var elegido=EstadiaRapidaSeleccionadaV22();
            var actual=_db.BuscarActiva(elegido.Ticket.ToString(CultureInfo.InvariantCulture));
            if(actual is null || actual.Id!=elegido.Id)
                throw new InvalidOperationException("El vehículo ya no está presente. Actualizá la búsqueda.");
            _cotizacionInicioV23=_db.CotizarSalida(actual.Id);
            _claveInicioV23=Guid.NewGuid();
            var c=_cotizacionInicioV23;
            InicioCobroDetalleText.Text=
                $"Ticket #{c.Ticket:D7} · {c.Patente} · {c.TipoVehiculo} · "+
                $"{actual.Sector} · Ingreso {actual.IngresoUtc.ToLocalTime():dd/MM HH:mm}";
            InicioCobroImporteText.Text="Total estimado: "+Dinero(c.ImporteCentavos);
            InicioCobroVigenciaText.Text=
                $"Vence a las {c.VenceUtc.ToLocalTime():HH:mm:ss}. "+
                "Se recalcula dentro de la transacción al confirmar; un cambio de tarifa/tiempo exige reconfirmar.";
            InicioCobroMedioCombo.SelectedIndex=0;
            InicioCobroRecibidoInput.Text=EditarImporte(c.ImporteCentavos);
            InicioCobroReferenciaInput.Text="";
            InicioCobroVerificadoCheck.IsChecked=false;
            InicioCobroEmitirXCheck.IsChecked=false;
            InicioCobroPanelV23.Visibility=Visibility.Visible;
            InicioConfirmarCobroButton.IsEnabled=c.TurnoId.HasValue;
            InicioCobroEstadoText.Text=c.TurnoId.HasValue
                ?"Confirmá únicamente si recibiste el dinero o verificaste el pago."
                :"No hay turno de caja abierto. Abrí uno desde Caja antes de cobrar.";
            DispatcherQueue.TryEnqueue(() =>
            {
                if(InicioPanel.Visibility==Visibility.Visible && _cotizacionInicioV23 is not null)
                {InicioCobroRecibidoInput.Focus(FocusState.Programmatic);InicioCobroRecibidoInput.SelectAll();}
            });
            InicioBusquedaEstadoText.Text="Salida preparada SIN COBRAR. Revisá medio, total y comprobante.";
        }
        catch(Exception ex)
        {
            CancelarPreparacionInicioV23();
            InicioBusquedaEstadoText.Text="Caja rápida no preparada: "+ex.Message;
        }
    }

    private string InicioCobroMedioSeleccionadoV23() =>
        (InicioCobroMedioCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??MediosPago.Efectivo;

    private void InicioCobroMedio_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(InicioCobroEfectivoPanel is null || InicioCobroElectronicoPanel is null)return;
        var efectivo=InicioCobroMedioSeleccionadoV23()==MediosPago.Efectivo;
        InicioCobroEfectivoPanel.Visibility=efectivo?Visibility.Visible:Visibility.Collapsed;
        InicioCobroElectronicoPanel.Visibility=efectivo?Visibility.Collapsed:Visibility.Visible;
        InicioCobroVueltoV23();
    }

    private void InicioCobroRecibido_TextChanged(object sender,TextChangedEventArgs e)=>
        InicioCobroVueltoV23();

    private void InicioCobroVueltoV23()
    {
        if(InicioCobroVueltoText is null)return;
        var c=_cotizacionInicioV23;
        if(c is null){InicioCobroVueltoText.Text="";return;}
        if(InicioCobroMedioSeleccionadoV23()!=MediosPago.Efectivo)
        {
            InicioCobroVueltoText.Text="Pago electrónico: "+Dinero(c.ImporteCentavos)+
                " (exacto; debe acreditarse y verificarse manualmente).";
            return;
        }
        try
        {
            var efectivo=LeerImporte(InicioCobroRecibidoInput.Text);
            InicioCobroVueltoText.Text=efectivo>=c.ImporteCentavos
                ?"Vuelto estimado: "+Dinero(efectivo-c.ImporteCentavos)
                :"Faltante: "+Dinero(c.ImporteCentavos-efectivo);
        }
        catch {InicioCobroVueltoText.Text="Ingresá el efectivo recibido para calcular el vuelto.";}
    }

    private void InicioCobroMedio_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter || InicioCobroMedioCombo.IsDropDownOpen)return;
        e.Handled=true;
        if(InicioCobroMedioSeleccionadoV23()==MediosPago.Efectivo)
            InicioCobroRecibidoInput.Focus(FocusState.Programmatic);
        else
            InicioCobroReferenciaInput.Focus(FocusState.Programmatic);
    }

    private void InicioCobroRecibido_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        InicioConfirmarCobroButton.Focus(FocusState.Programmatic);
    }

    private void InicioCobroReferencia_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        InicioCobroVerificadoCheck.Focus(FocusState.Programmatic);
    }

    private void InicioCobroVerificado_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        // La verificación externa SOLO se tilda con Espacio o clic del operador.
        InicioConfirmarCobroButton.Focus(FocusState.Programmatic);
    }

    private void InicioIrFormularioSalida_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Cobro);
            if(_cotizacionInicioV23 is null)throw new InvalidOperationException("Prepará primero la salida.");
            var ticket=_cotizacionInicioV23.Ticket;
            CancelarPreparacionInicioV23();
            Navegacion.SelectedItem=Navegacion.MenuItems[2];
            BusquedaInput.Text=ticket.ToString(CultureInfo.InvariantCulture);
            Buscar_Click(sender,new RoutedEventArgs());
            Mensaje.Text="Salida abierta en el formulario completo. Aún NO se cobró.";
        }
        catch(Exception ex){InicioBusquedaEstadoText.Text="No se pudo abrir la salida: "+ex.Message;}
    }

    private void InicioCancelarCobro_Click(object sender,RoutedEventArgs e)
    {
        CancelarPreparacionInicioV23();
        InicioBusquedaEstadoText.Text="Se canceló la preparación. No se generó ningún cobro.";
    }

    private void InicioConfirmarCobro_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoInicioV23 || _cotizacionInicioV23 is null ||
           _claveInicioV23==Guid.Empty || !InicioConfirmarCobroButton.IsEnabled)return;

        var c=_cotizacionInicioV23;
        var medio=InicioCobroMedioSeleccionadoV23();
        var referencia=InicioCobroReferenciaInput.Text;
        bool verificado=InicioCobroVerificadoCheck.IsChecked==true;
        long entregado;
        string? talonarioX;
        try
        {
            entregado=medio==MediosPago.Efectivo
                ?LeerImporte(InicioCobroRecibidoInput.Text):c.ImporteCentavos;
            PoliticaCobroRapidoV23.Validar(c.ImporteCentavos,medio,entregado,
                referencia,verificado);
            // No registrar el pago si faltan talonario o impresora del X solicitado.
            talonarioX=PrepararXAntesDelCobro(InicioCobroEmitirXCheck.IsChecked==true);
        }
        catch(Exception ex)
        {
            InicioCobroEstadoText.Text="NO se cobró: "+ex.Message;
            return;
        }
        _procesandoInicioV23=true;
        InicioConfirmarCobroButton.IsEnabled=false;
        CobroConfirmadoV4? cobro=null;
        try
        {
            cobro=_db.ConfirmarPago(c,medio,entregado,referencia,verificado,_claveInicioV23);
            // Se invalida inmediatamente la cotización, antes de refrescar o imprimir.
            _cotizacionInicioV23=null;
            _claveInicioV23=Guid.Empty;
            var resumen=$"Cobro #{cobro.Ticket:D7} CONFIRMADO · {medio} · "+
                Dinero(cobro.ImporteCentavos)+
                (medio==MediosPago.Efectivo?$" · Vuelto: {Dinero(cobro.VueltoCentavos)}":
                    " · Pago externo verificado por operador");
            InicioCobroEstadoText.Text=resumen;
            Mensaje.Text=resumen;
            InicioCobroImporteText.Text="";
            InicioCobroVigenciaText.Text="";
            InicioCobroEmitirXCheck.IsChecked=false;
            InicioCobroVerificadoCheck.IsChecked=false;
            InicioCobroRecibidoInput.Text="";
            InicioCobroReferenciaInput.Text="";
            try {Refrescar();ActualizarCaja();}
            catch(Exception ex)
            {
                InicioCobroEstadoText.Text+=". No se pudo actualizar la vista: "+ex.Message+
                    ". El cobro SIGUE CONFIRMADO; no lo repitas.";
            }
            if(talonarioX is not null)
            {
                var resultadoX=EmitirXDespuesDelCobro(talonarioX,"Estacionamiento",cobro.Id);
                InicioCobroEstadoText.Text+=". "+resultadoX;
                Mensaje.Text+=". "+resultadoX;
            }
            else if(_autoImprimirCobro)
            {
                ImprimirCobroSeguro(cobro);
                InicioCobroEstadoText.Text+=". "+SalidaResultadoReciboText.Text;
            }
            else
                InicioCobroEstadoText.Text+=". Recibo no impreso por configuración. "+
                    "Podés imprimirlo desde Salida y cobro > Historial.";
            InicioBusquedaEstadoText.Text=
                $"Ticket #{cobro.Ticket:D7} finalizado y cobrado. Buscá otro vehículo.";
            _coincidenciasRapidasV22=Array.Empty<Estadia>();
            InicioCoincidenciasList.ItemsSource=null;
            InicioCoincidenciasList.Visibility=Visibility.Collapsed;
            InicioOpcionesBusquedaPanel.Visibility=Visibility.Collapsed;
            InicioBusquedaInput.Text="";
            InicioCobroPanelV23.Visibility=Visibility.Collapsed;
            InicioCobroEmitirXCheck.IsChecked=false;
            InicioPanelEstadoText.Text=InicioCobroEstadoText.Text;
            InicioBusquedaInput.Focus(FocusState.Programmatic);
        }
        catch(Exception ex)
        {
            InicioCobroEstadoText.Text=cobro is null
                ?"No se pudo confirmar o verificar el cobro: "+ex.Message+
                    ". Revisá caja y estadía antes de volver a intentarlo."
                :$"Cobro #{cobro.Ticket:D7} YA CONFIRMADO, pero falló una acción posterior: "+
                    ex.Message+". No vuelvas a cobrar.";
            InicioPanelEstadoText.Text=InicioCobroEstadoText.Text;
            _cotizacionInicioV23=null;
            _claveInicioV23=Guid.Empty;
            InicioCobroEmitirXCheck.IsChecked=false;
        }
        finally
        {
            _procesandoInicioV23=false;
            InicioConfirmarCobroButton.IsEnabled=false;
        }
    }

    private void ActualizarSectores()
    {
        try
        {
            _sectoresConfigurados=_db.ListarSectores();
            var actual=SectorInput.Text;
            SectoresIngresoCombo.Items.Clear();
            foreach(var sec in _sectoresConfigurados.Where(x=>x.Activo))
                SectoresIngresoCombo.Items.Add(new ComboBoxItem{
                    Content=$"{sec.Nombre} · {sec.Libres} libres / {sec.Capacidad} · {sec.Modo}",
                    Tag=sec.Nombre
                });
            var i=_sectoresConfigurados.Where(x=>x.Activo)
                .Select((sector,indice)=>new{sector,indice})
                .FirstOrDefault(x=>string.Equals(x.sector.Nombre,actual,StringComparison.OrdinalIgnoreCase))?.indice??0;
            if(SectoresIngresoCombo.Items.Count>0)
                SectoresIngresoCombo.SelectedIndex=Math.Min(i,SectoresIngresoCombo.Items.Count-1);
            ConfigSectoresList.ItemsSource=_sectoresConfigurados.Select(x=>
                $"{x.Nombre} · {x.Ocupados}/{x.Capacidad} ocupadas · {x.Libres} libres · {x.Modo}" +
                (x.Activo?"":" · DESHABILITADO")).ToList();
            ConfigOcupacionText.Text=$"TOTAL: {_sectoresConfigurados.Sum(x=>x.Ocupados)} ocupados / " +
                $"{_sectoresConfigurados.Sum(x=>x.Capacidad)} de capacidad nominal.";
        }
        catch(Exception ex){Mensaje.Text="Sectores: "+ex.Message;}
    }

    private void SectoresIngresoCombo_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(SectorInput is null || IngresoSectorDisponibilidadText is null)return;
        var nombre=(SectoresIngresoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if(string.IsNullOrWhiteSpace(nombre))return;
        SectorInput.Text=nombre;
        var sector=_sectoresConfigurados.FirstOrDefault(x=>x.Nombre==nombre);
        IngresoSectorDisponibilidadText.Text=sector is null?"":$"Sector {sector.Nombre}: " +
            $"{sector.Libres} lugares disponibles · {sector.Modo}";
        if(sector?.Modo==ModosSector.Libre)PlazaIngresoInput.Text="";
        PlazaIngresoInput.IsEnabled=sector?.Modo==ModosSector.Asignado;
    }

    private void ConfigSectoresList_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(ConfigSectoresList.SelectedIndex<0 ||
           ConfigSectoresList.SelectedIndex>=_sectoresConfigurados.Count)return;
        var sector=_sectoresConfigurados[ConfigSectoresList.SelectedIndex];
        ConfigSectorNombreInput.Text=sector.Nombre;
        ConfigSectorCapacidadInput.Text=sector.Capacidad.ToString(CultureInfo.InvariantCulture);
        ConfigSectorModoCombo.SelectedIndex=sector.Modo==ModosSector.Asignado?1:0;
        ConfigSectorActivoCheck.IsChecked=sector.Activo;
        ActualizarListaPlazas();
    }

    private void ActualizarListaPlazas()
    {
        int indice=ConfigSectoresList.SelectedIndex;
        if(indice<0 || indice>=_sectoresConfigurados.Count)return;
        try
        {
            ConfigPlazasList.ItemsSource=_db.ListarPlazas(_sectoresConfigurados[indice].Nombre)
                .Select(p=>$"{p.Codigo} · {(p.Habilitada?"Habilitada":"Inhabilitada")}" +
                    (p.Ocupada?$" · OCUPADA: {p.Patente}":" · Libre")).ToList();
        }
        catch(Exception ex){Mensaje.Text="Plazas: "+ex.Message;}
    }

    private void GuardarPoliticaCaja_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var obligatoria=CajaCiegaObligatoriaCheck.IsChecked==true;
            _db.ConfigurarCajaCiegaObligatoria(obligatoria);
            CajaCiegaTurnoCheck.IsChecked=obligatoria || CajaCiegaTurnoCheck.IsChecked==true;
            CajaCiegaTurnoCheck.IsEnabled=!obligatoria;
            Mensaje.Text=obligatoria?"Caja ciega obligatoria activada para próximos turnos.":
                "Política de caja ciega opcional para próximos turnos.";
        }
        catch(Exception ex){Mensaje.Text="No se cambió la política: "+ex.Message;}
    }

    private void GuardarSector_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!int.TryParse(ConfigSectorCapacidadInput.Text,out var capacidad))
                throw new ArgumentException("Indicá una capacidad válida.");
            var modo=(ConfigSectorModoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
            _db.GuardarSector(ConfigSectorNombreInput.Text,capacidad,modo,
                ConfigSectorActivoCheck.IsChecked==true);
            Mensaje.Text="Configuración de sector guardada.";
            ActualizarSectores();
        }
        catch(Exception ex){Mensaje.Text="Sector no guardado: "+ex.Message;}
    }

    private void GuardarPlaza_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var indice=ConfigSectoresList.SelectedIndex;
            if(indice<0 || indice>=_sectoresConfigurados.Count)
                throw new ArgumentException("Seleccioná primero un sector.");
            _db.GuardarPlaza(_sectoresConfigurados[indice].Nombre,
                ConfigPlazaCodigoInput.Text,ConfigPlazaHabilitadaCheck.IsChecked==true);
            Mensaje.Text="Plaza guardada.";
            ActualizarListaPlazas();
            ActualizarSectores();
        }
        catch(Exception ex){Mensaje.Text="Plaza no guardada: "+ex.Message;}
    }

    private void Ingresar_Click(object sender,RoutedEventArgs e)
    {
        if(_guardandoIngreso)return;
        _guardandoIngreso=true;
        GuardarIngresoButton.IsEnabled=false;
        long? numero=null;
        try
        {
            var tipo=(TipoInput.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";
            // Esta operación finaliza el alta ANTES de tocar el spooler.
            numero=_db.RegistrarIngreso(PatenteInput.Text,tipo,SectorInput.Text,
                string.IsNullOrWhiteSpace(PlazaIngresoInput.Text)?null:PlazaIngresoInput.Text);
            Mensaje.Text=$"Ingreso #{numero.Value:D7} GUARDADO en la base de datos.";
            PatenteInput.Text="";
            PlazaIngresoInput.Text="";
            try
            {
                var ticket=_db.ObtenerTicket(numero.Value);
                TicketPreviewText.Text=$"Ticket {ticket.Numero:D7} · {ticket.Patente} · "+
                    $"{ticket.TipoVehiculo} · {ticket.Sector}"+
                    (ticket.Plaza is null?"":$" / plaza {ticket.Plaza}")+
                    $" · {ticket.FechaIngresoUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
                Refrescar();
                ActualizarSectores();
            }
            catch(Exception ex)
            {
                Mensaje.Text+=$" La actualización visual falló: {ex.Message}. "+
                    "El ingreso YA ESTÁ GUARDADO. No lo registres nuevamente.";
            }
            if(_autoImprimirIngreso)
                ImprimirTicketSeguro(numero.Value);
        }
        catch(Exception ex)
        {
            Mensaje.Text=numero.HasValue
                ?$"Ingreso #{numero.Value:D7} YA GUARDADO; error posterior: {ex.Message}. "+
                    "No vuelvas a registrarlo."
                :"No se pudo completar el ingreso: "+ex.Message+
                    ". Revisá vehículos presentes antes de volver a intentar.";
        }
        finally
        {
            _guardandoIngreso=false;
            GuardarIngresoButton.IsEnabled=RolesAparcar.Permite(
                _db.SesionActual?.Rol??"",PermisosAparcar.Ingreso);
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
            SalidaResultadoReciboText.Text="";
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
                $"\nTarifa versión {_cotizacion.TarifaVersion}" +
                (_cotizacion.CubiertoPorAbono?"\nABONO VIGENTE: cobertura total":"");
            SalidaImporteText.Text = Dinero(_cotizacion.ImporteCentavos);
            CotizacionVigenciaText.Text = "Cotización válida hasta " +
                _cotizacion.VenceUtc.ToLocalTime().ToString("HH:mm:ss");
            ConfirmarSalidaButton.IsEnabled = _cotizacion.TurnoId.HasValue;
            Mensaje.Text = _cotizacion.TurnoId.HasValue
                ? "Contá el efectivo y confirmá el cobro."
                : "Para cobrar primero abrí un turno en Caja.";
            MedioPagoCombo.SelectedIndex=0;
            EfectivoRecibidoInput.Text = EditarImporte(_cotizacion.ImporteCentavos);
            ReferenciaElectronicaInput.Text = "";
            PagoVerificadoCheck.IsChecked = false;
            VueltoPrevistoText.Text = "Vuelto a entregar: "+Dinero(0);
            DispatcherQueue.TryEnqueue(() =>
            {
                if(SalidaPanel.Visibility==Visibility.Visible && _cotizacion is not null)
                {EfectivoRecibidoInput.Focus(FocusState.Programmatic);EfectivoRecibidoInput.SelectAll();}
            });
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

    private string MedioSeleccionado() =>
        (MedioPagoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? MediosPago.Efectivo;

    private void MedioPagoCombo_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(EfectivoPanel is null || ElectronicoPanel is null)return;
        var cash=MedioSeleccionado()==MediosPago.Efectivo;
        EfectivoPanel.Visibility=cash?Visibility.Visible:Visibility.Collapsed;
        ElectronicoPanel.Visibility=cash?Visibility.Collapsed:Visibility.Visible;
        if(_cotizacion is null)return;
        VueltoPrevistoText.Text=cash ? "" :
            "Total externo exacto por verificar: "+Dinero(_cotizacion.ImporteCentavos);
    }

    private void ConfirmarSalida_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoSalida || _cotizacion is null || _claveCobro==Guid.Empty)return;
        var medio=MedioSeleccionado();
        long entregado;
        try
        {
            entregado=medio==MediosPago.Efectivo
                ?LeerImporte(EfectivoRecibidoInput.Text)
                :_cotizacion.ImporteCentavos;
        }
        catch(Exception ex){Mensaje.Text="Importe inválido: "+ex.Message;return;}

        string? talonarioX;
        try
        {
            PoliticaCobroRapidoV23.Validar(_cotizacion.ImporteCentavos,medio,entregado,
                ReferenciaElectronicaInput.Text,PagoVerificadoCheck.IsChecked==true);
            talonarioX=PrepararXAntesDelCobro(SalidaEmitirXCheck.IsChecked==true);
        }
        catch(Exception ex)
        {
            Mensaje.Text="No se confirmó el pago: "+ex.Message;
            return;
        }
        _procesandoSalida=true;
        ConfirmarSalidaButton.IsEnabled=false;
        CobroConfirmadoV4? cobro=null;
        try
        {
            // Transacción idempotente; cualquier operación posterior es SOLO presentación.
            cobro=_db.ConfirmarPago(_cotizacion,medio,entregado,
                ReferenciaElectronicaInput.Text,PagoVerificadoCheck.IsChecked==true,_claveCobro);
            // Invalidar la cotización ANTES de imprimir y refrescar la interfaz.
            _cotizacion=null;
            _estadiaSeleccionada=null;
            _claveCobro=Guid.Empty;
            Mensaje.Text=$"Cobro #{cobro.Ticket:D7} CONFIRMADO por {medio}: "+
                Dinero(cobro.ImporteCentavos)+
                (medio==MediosPago.Efectivo?$" · Vuelto {Dinero(cobro.VueltoCentavos)}.":
                    " · Verificación externa declarada.");
            SalidaDetalleText.Text="Cobro y salida confirmados. Buscá el siguiente vehículo.";
            SalidaImporteText.Text="";
            CotizacionVigenciaText.Text="";
            VueltoPrevistoText.Text="";
            BusquedaInput.Text="";
            EfectivoRecibidoInput.Text="";
            ReferenciaElectronicaInput.Text="";
            PagoVerificadoCheck.IsChecked=false;
            try
            {
                Refrescar();
                ActualizarCaja();
            }
            catch(Exception ex)
            {
                Mensaje.Text+=" La vista de caja no se actualizó: "+ex.Message+
                    ". El cobro sigue CONFIRMADO; no lo repitas.";
            }
            if(talonarioX is not null)
            {
                var resultadoX=EmitirXDespuesDelCobro(
                    talonarioX,"Estacionamiento",cobro.Id);
                SalidaResultadoReciboText.Text=resultadoX;
                Mensaje.Text+=" "+resultadoX;
            }
            else if(_autoImprimirCobro)
                ImprimirCobroSeguro(cobro);
            else
                SalidaResultadoReciboText.Text=
                    "Cobro confirmado. Impresión automática desactivada: "+
                    "elegí el recibo en el historial para imprimir ORIGINAL.";
            SalidaEmitirXCheck.IsChecked=false;
            ActualizarRecibosCobroV21(cobro.Id);
            BusquedaInput.Focus(FocusState.Programmatic);
        }
        catch(Exception ex)
        {
            Mensaje.Text=cobro is null
                ?"No se pudo confirmar o verificar el estado del cobro: "+ex.Message+
                    ". Consultá la estadía y caja antes de reintentar."
                :$"Cobro #{cobro.Ticket:D7} YA CONFIRMADO. Falló una acción posterior: "+
                    ex.Message+". No vuelvas a cobrar.";
            ConfirmarSalidaButton.IsEnabled=false;
            _cotizacion=null;
            _claveCobro=Guid.Empty;
        }
        finally
        {
            _procesandoSalida=false;
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

    private void MedioPagoCombo_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter || MedioPagoCombo.IsDropDownOpen)return;
        e.Handled=true;
        if(MedioSeleccionado()==MediosPago.Efectivo)
            EfectivoRecibidoInput.Focus(FocusState.Programmatic);
        else
            ReferenciaElectronicaInput.Focus(FocusState.Programmatic);
    }

    private void ReferenciaElectronicaInput_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        // Solo se avanza el foco. Aparcar jamás verifica un pago automáticamente.
        PagoVerificadoCheck.Focus(FocusState.Programmatic);
    }

    private void PagoVerificadoCheck_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        // La aprobación explícita se activa con Espacio o clic del operador.
        ConfirmarSalidaButton.Focus(FocusState.Programmatic);
    }

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

    private void ActualizarTarifasSector()
    {
        try
        {
            var anterior=(TarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            TarifaSectorCombo.Items.Clear();
            foreach(var sector in _db.ListarSectores())
                TarifaSectorCombo.Items.Add(new ComboBoxItem{Content=sector.Nombre,Tag=sector.Nombre});
            var opciones=TarifaSectorCombo.Items.Cast<ComboBoxItem>().ToList();
            var actual=opciones.FirstOrDefault(x=>x.Tag?.ToString()==anterior)??opciones.FirstOrDefault();
            if(actual is not null)TarifaSectorCombo.SelectedItem=actual;
            MostrarTarifaSectorSeleccionada();
        }
        catch(Exception ex){Mensaje.Text="Tarifas por sector: "+ex.Message;}
    }

    private void TarifaSectorCombo_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_db is null || TarifaSectorCombo is null || TarifaSectorTipoCombo is null ||
           TarifaFranjaCombo is null || TarifaSectorVersionText is null)return;
        MostrarTarifaSectorSeleccionada();
    }

    private void MostrarTarifaSectorSeleccionada()
    {
        var sector=(TarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if(string.IsNullOrEmpty(sector))return;
        var tipo=(TarifaSectorTipoCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()??"Auto";
        var franja=(TarifaFranjaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??FranjasTarifarias.Diurna;
        try
        {
            var horario=_db.ObtenerHorarioTarifario(sector);
            HorarioNocheInicioInput.Text=horario.InicioNoche.ToString("HH:mm");
            HorarioNocheFinInput.Text=horario.FinNoche.ToString("HH:mm");
            var vigente=_db.ObtenerTarifaSector(sector,tipo,franja);
            TarifaSectorVersionText.Text=vigente is null
                ?"Sin tarifa específica: se utilizará la general vigente al ingreso."
                :$"Tarifa de {sector}/{tipo}/{franja}: versión {vigente.Version}.";
            var baseTarifa=vigente?.ComoTarifaOperativa() ??
                _db.ListarTarifas().Single(x=>x.TipoVehiculo==tipo);
            TarifaSectorPrimeraInput.Text=EditarImporte(baseTarifa.PrimeraHoraCentavos);
            TarifaSectorFraccionInput.Text=EditarImporte(baseTarifa.FraccionCentavos);
            TarifaSectorMinutosInput.Text=baseTarifa.MinutosFraccion.ToString();
            TarifaSectorTopeInput.Text=EditarImporte(baseTarifa.Tope24HorasCentavos);
            TarifaSectorCortesiaInput.Text=baseTarifa.CortesiaMinutos.ToString();
        }
        catch(Exception ex){Mensaje.Text="Tarifa por sector: "+ex.Message;}
    }

    private static TimeOnly LeerHorario(string texto)
    {
        if(!TimeOnly.TryParseExact(texto.Trim(),"HH:mm",CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var horario))
            throw new ArgumentException("Escribí el horario en formato HH:mm (ej.: 20:00).");
        return horario;
    }

    private void GuardarHorario_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var sector=(TarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??
                throw new ArgumentException("Elegí un sector.");
            var nuevo=_db.ConfigurarHorarioTarifario(sector,
                LeerHorario(HorarioNocheInicioInput.Text),LeerHorario(HorarioNocheFinInput.Text));
            Mensaje.Text=$"Horario nocturno de {nuevo.Sector} guardado. Se aplica a próximos ingresos.";
            MostrarTarifaSectorSeleccionada();
        }
        catch(Exception ex){Mensaje.Text="Horario no guardado: "+ex.Message;}
    }

    private void GuardarTarifaSector_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var sector=(TarifaSectorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??
                throw new ArgumentException("Elegí un sector.");
            var tipo=(TarifaSectorTipoCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";
            var franja=(TarifaFranjaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
            if(!int.TryParse(TarifaSectorMinutosInput.Text,out var minutos)||
               !int.TryParse(TarifaSectorCortesiaInput.Text,out var cortesia))
                throw new ArgumentException("Minutos de fracción y cortesía inválidos.");
            var tarifa=_db.GuardarTarifaSector(sector,tipo,franja,
                LeerImporte(TarifaSectorPrimeraInput.Text),
                LeerImporte(TarifaSectorFraccionInput.Text),minutos,
                LeerImporte(TarifaSectorTopeInput.Text),cortesia);
            Mensaje.Text=$"Tarifa {tarifa.Sector}/{tarifa.Franja} v{tarifa.Version} guardada para futuros ingresos.";
            MostrarTarifaSectorSeleccionada();
        }
        catch(Exception ex){Mensaje.Text="Tarifa sectorial no guardada: "+ex.Message;}
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
                var ciego=_db.EsTurnoCiego(turno.Id);
                var rolActual=_db.SesionActual?.Rol;
                var oculto=ciego && rolActual is not (RolesAparcar.Administrador or RolesAparcar.Supervisor);
                if(oculto)
                    CajaEstadoText.Text=$"Turno de {turno.Operador} desde {turno.AperturaUtc.ToLocalTime():dd/MM HH:mm}" +
                        "\nCAJA CIEGA: el total esperado está oculto. Ingresá el contado antes del cierre.";
                else
                {
                    var resumen=_db.ObtenerResumenCaja()!;
                    CajaEstadoText.Text=$"Turno de {turno.Operador} desde {turno.AperturaUtc.ToLocalTime():dd/MM HH:mm}" +
                    $"\nFondo {Dinero(resumen.FondoInicialCentavos)} · Efectivo cobrado {Dinero(resumen.CobrosEfectivoCentavos)}" +
                    $" · Electrónicos {Dinero(resumen.CobrosElectronicosCentavos)}" +
                    $"\nDepósitos {Dinero(resumen.DepositosCentavos)} · Retiros {Dinero(resumen.RetirosCentavos)}" +
                    $" · Devoluciones {Dinero(resumen.DevolucionesEfectivoCentavos)}" +
                    $"\nEFECTIVO ESPERADO: {Dinero(resumen.EfectivoEsperadoCentavos)}";
                }
                AbrirCajaButton.IsEnabled = false;
                CerrarCajaButton.IsEnabled = true;
            }
            CobrosCajaList.ItemsSource = _db.ListarCobros(60).Select(c =>
                $"#{c.Ticket:D7} · {c.Patente} · {Dinero(c.ImporteCentavos)}" +
                $" · {c.FechaUtc.ToLocalTime():dd/MM HH:mm} · {c.Operador}").ToList();
            MovimientosCajaList.ItemsSource=_db.ListarMovimientosManuales(30).Select(m=>
                $"{m.FechaUtc.ToLocalTime():dd/MM HH:mm} · {m.Tipo} · {Dinero(m.ImporteCentavos)} · {m.Motivo}").ToList();
            CobrosDevolucionCombo.Items.Clear();
            var rol=_db.SesionActual?.Rol??"";
            var puedeDevolver=RolesAparcar.Permite(rol,PermisosAparcar.Devolucion);
            CobrosDevolucionCombo.IsEnabled=puedeDevolver;
            if(puedeDevolver)
                foreach(var c in _db.CobrosEfectivoParaDevolver(100).Where(c=>!c.Devuelto))
                    CobrosDevolucionCombo.Items.Add(new ComboBoxItem{
                        Content=$"#{c.Ticket:D7} · {c.Patente} · {Dinero(c.ImporteCentavos)}",
                        Tag=c.Id.ToString("D")
                    });
            DevolucionComplementoCombo.Items.Clear();
            DevolucionComplementoCombo.IsEnabled=puedeDevolver;
            if(puedeDevolver)
                foreach(var c in _db.ListarComplementosReembolsables())
                    DevolucionComplementoCombo.Items.Add(new ComboBoxItem{
                        Content=$"{c.Categoria} · {c.Patente} · {Dinero(c.ImporteCentavos)}",
                        Tag=c.CobroId.ToString("D")
                    });
            CrearPinButton.IsEnabled=
                RolesAparcar.Permite(rol,PermisosAparcar.Usuarios) &&
                !_db.ExistePinSupervisor();
        }
        catch (Exception ex) { Mensaje.Text = "Error al cargar caja: "+ex.Message; }
    }

    private void AbrirCaja_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var turno = _db.AbrirTurno(OperadorCajaInput.Text,
                LeerImporte(FondoInicialInput.Text),CajaCiegaTurnoCheck.IsChecked==true);
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
            var ciega=_db.EsTurnoCiego(turno.Id);
            var rolActual=_db.SesionActual?.Rol;
            var oculto=ciega && rolActual is not (RolesAparcar.Administrador or RolesAparcar.Supervisor);
            var resumenConfirmacion=oculto
                ?$"CAJA CIEGA: efectivo contado {Dinero(contado)}. El esperado y la diferencia se calculan después de confirmar."
                :$"Efectivo esperado {Dinero(turno.EsperadoCentavos)}; contado {Dinero(contado)}; diferencia {Dinero(contado-turno.EsperadoCentavos)}.";
            var dialogo=new ContentDialog
            {
                Title="Confirmar cierre de caja",
                Content=resumenConfirmacion+"\nEl turno quedará cerrado para nuevos cobros.",
                PrimaryButtonText="Confirmar cierre",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            var arqueo=_db.CerrarTurno(turno.Id,contado);
            UltimoArqueoText.Text=$"Turno cerrado. Esperado {Dinero(arqueo.EsperadoCentavos)}" +
                $" · Contado {Dinero(arqueo.ContadoCentavos)} · Diferencia {Dinero(arqueo.DiferenciaCentavos)}"+
                $" · Cierre Z N° {arqueo.NumeroCierreZ:D8}.";
            Mensaje.Text=$"Turno cerrado y Cierre Z N° {arqueo.NumeroCierreZ:D8} guardado.";
            ActualizarCaja();
            ActualizarCierresZ();
        }
        catch(Exception ex){ Mensaje.Text="No se pudo cerrar caja: "+ex.Message; }
    }

    private void ImprimirCobroSeguro(CobroConfirmadoV4 cobro,bool copia=false,
        string motivo="",bool devuelto=false)
    {
        Guid intento=Guid.Empty;
        bool enviado=false;
        try
        {
            // Persistir antes de enviarlo al driver: evita originales duplicados,
            // inclusive tras reinicio, error del spooler o pérdida de energía.
            intento=_db.RegistrarIntentoReciboCobroV21(cobro.Id,copia,motivo,_impresora??"");
            var recibo=new CobroEfectivoConfirmado(cobro.Id,cobro.EstadiaId,cobro.Ticket,
                cobro.ImporteCentavos,cobro.EntregadoCentavos,cobro.VueltoCentavos,cobro.FechaUtc);
            TicketPrinter.ImprimirCobro(recibo,_impresora??"",_anchoTicket,
                cobro.MedioPago,copia,devuelto);
            enviado=true;
            _db.RegistrarResultadoReciboCobroV21(intento,true,
                "Trabajo entregado al controlador Windows; salida en papel no confirmada.");
            var texto=(copia?"COPIA":"ORIGINAL")+
                $" del recibo #{cobro.Ticket:D7} enviada al controlador. "+
                "No se creó otro cobro y no se confirma impresión física.";
            SalidaResultadoReciboText.Text=texto;
            Mensaje.Text+=" "+texto;
        }
        catch(Exception ex)
        {
            if(intento!=Guid.Empty && !enviado)
            {
                try { _db.RegistrarResultadoReciboCobroV21(intento,false,ex.Message); }
                catch { /* Conservar el problema inicial, incluidos errores de auditoría. */ }
            }
            var texto=$"El cobro #{cobro.Ticket:D7} SIGUE CONFIRMADO. "+
                (enviado?"El recibo fue enviado al controlador, pero no se pudo actualizar su registro: ":
                    "No se pudo completar el envío del recibo: ")+ex.Message+
                ". NO repitas el cobro; consultá el historial y revisá la impresora.";
            SalidaResultadoReciboText.Text=texto;
            Mensaje.Text+=" "+texto;
        }
    }

    private void ActualizarRecibosCobroV21(Guid? seleccionar=null)
    {
        try
        {
            var anterior=seleccionar?.ToString("D") ??
                (RecibosCobroCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            RecibosCobroCombo.Items.Clear();
            foreach(var recibo in _db.ListarRecibosCobroV21(100))
            {
                var c=recibo.Cobro;
                var item=new ComboBoxItem
                {
                    Tag=c.Id.ToString("D"),
                    Content=$"{c.FechaUtc.ToLocalTime():dd/MM HH:mm} · "+
                        $"#{c.Ticket:D7} · {c.MedioPago} · {Dinero(c.ImporteCentavos)} · "+
                        (recibo.Devuelto?"DEVUELTO · ":"")+
                        (recibo.TieneIntentoOriginal?
                            $"Original registrado ({recibo.EstadoUltimaImpresion??"Pendiente"})":
                            "Sin intento original registrado")
                };
                RecibosCobroCombo.Items.Add(item);
                if(item.Tag?.ToString()==anterior)RecibosCobroCombo.SelectedItem=item;
            }
            ReciboCobroHistorialEstadoText.Text=
                $"Últimos cobros consultados: {RecibosCobroCombo.Items.Count}. "+
                "Una copia requiere motivo y permiso de supervisor. "+
                "Si ya se imprimió con una versión anterior, revisá el papel antes de solicitar ORIGINAL.";
        }
        catch(Exception ex)
        {
            RecibosCobroCombo.Items.Clear();
            ReciboCobroHistorialEstadoText.Text="No se pudo consultar el historial: "+ex.Message;
        }
    }

    private void ReciboCobroActualizar_Click(object sender,RoutedEventArgs e)=>
        ActualizarRecibosCobroV21();

    private ReciboCobroV21 ReciboCobroElegidoV21()
    {
        if(!Guid.TryParse((RecibosCobroCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var cobroId))
            throw new ArgumentException("Elegí un cobro confirmado del historial.");
        return _db.ObtenerReciboCobroV21(cobroId);
    }

    private void PrepararFacturacionDesdeMovimiento(string origen,Guid cobroId,bool consumidorFinal)
    {
        _db.ExigirPermiso(PermisosAparcar.Configuracion);
        var pendiente=_db.ListarPendientesFiscales(1000)
            .FirstOrDefault(x=>x.CobroId==cobroId && x.Origen==origen &&
                x.Estado=="PendienteDatosFiscales");
        if(pendiente is null)
            throw new InvalidOperationException(
                "Este cobro ya no está pendiente de facturación o todavía no tiene un movimiento fiscal disponible.");

        Navegacion.SelectedItem=Navegacion.MenuItems[9];
        ConfigSectionNavigation.SelectedIndex=6;
        var tag=pendiente.Origen+"|"+pendiente.CobroId.ToString("D");
        var item=FiscalCobroCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x=>string.Equals(x.Tag?.ToString(),tag,StringComparison.OrdinalIgnoreCase));
        if(item is null)
        {
            RefrescarFiscalHomologacion();
            item=FiscalCobroCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x=>string.Equals(x.Tag?.ToString(),tag,StringComparison.OrdinalIgnoreCase));
        }
        if(item is null)throw new InvalidOperationException("No se pudo cargar el cobro en la pantalla fiscal.");
        FiscalCobroCombo.SelectedItem=item;
        FiscalIdentificarPorSolicitudClienteCheck.IsChecked=!consumidorFinal;
        FiscalResultadoText.Text=consumidorFinal
            ? "Cobro cargado. Consultá condiciones IVA y aplicá Consumidor Final desde el catálogo oficial antes de preparar la factura."
            : "Cobro cargado. Completá o seleccioná los datos del cliente identificado antes de preparar la factura.";
    }

    private void PrepararFacturacionDesdeDetalle(bool consumidorFinal)
    {
        var recibo=ReciboCobroElegidoV21();
        PrepararFacturacionDesdeMovimiento("Estacionamiento",recibo.Cobro.Id,consumidorFinal);
    }

    private void FacturarCobroSeleccionado_Click(object sender,RoutedEventArgs e)
    {
        try { PrepararFacturacionDesdeDetalle(false); }
        catch(Exception ex){ ReciboCobroHistorialEstadoText.Text="No se abrió la facturación: "+ex.Message; }
    }

    private void FacturarConsumidorFinal_Click(object sender,RoutedEventArgs e)
    {
        try { PrepararFacturacionDesdeDetalle(true); }
        catch(Exception ex){ ReciboCobroHistorialEstadoText.Text="No se abrió la facturación: "+ex.Message; }
    }

    private void FacturarLavadoSeleccionado_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var orden=OrdenSeleccionada();
            var cobro=_db.BuscarCobroComplementario("Lavadero",orden.Id)
                ??throw new InvalidOperationException("La orden todavía no tiene un cobro confirmado.");
            PrepararFacturacionDesdeMovimiento("Lavadero",cobro.Id,false);
        }
        catch(Exception ex){Mensaje.Text="No se abrió la facturación del lavado: "+ex.Message;}
    }

    private void FacturarLavadoConsumidorFinal_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var orden=OrdenSeleccionada();
            var cobro=_db.BuscarCobroComplementario("Lavadero",orden.Id)
                ??throw new InvalidOperationException("La orden todavía no tiene un cobro confirmado.");
            PrepararFacturacionDesdeMovimiento("Lavadero",cobro.Id,true);
        }
        catch(Exception ex){Mensaje.Text="No se abrió la facturación del lavado: "+ex.Message;}
    }

    private void FacturarAbonoSeleccionado_Click(object sender,RoutedEventArgs e) =>
        FacturarAbonoSeleccionado(false);

    private void FacturarAbonoConsumidorFinal_Click(object sender,RoutedEventArgs e) =>
        FacturarAbonoSeleccionado(true);

    private void FacturarAbonoSeleccionado(bool consumidorFinal)
    {
        try
        {
            var contrato=ContratoSeleccionado();
            if(!Guid.TryParse(contrato.ReferenciaCobro,out var cobroId))
                throw new InvalidOperationException("El contrato todavía no tiene un cobro confirmado.");
            PrepararFacturacionDesdeMovimiento("Abono",cobroId,consumidorFinal);
        }
        catch(Exception ex){Mensaje.Text="No se abrió la facturación del abono: "+ex.Message;}
    }

    private void ReciboCobroImprimirOriginal_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoImpresionCobro)return;
        _procesandoImpresionCobro=true;
        ReciboCobroImprimirOriginalButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Impresion);
            var recibo=ReciboCobroElegidoV21();
            if(recibo.TieneIntentoOriginal)
                throw new InvalidOperationException(
                    "Ya existe un intento de ORIGINAL. Usá COPIA autorizada.");
            ImprimirCobroSeguro(recibo.Cobro,false,"",recibo.Devuelto);
            ActualizarRecibosCobroV21(recibo.Cobro.Id);
        }
        catch(Exception ex)
        {
            ReciboCobroHistorialEstadoText.Text="No se envió ORIGINAL: "+ex.Message;
        }
        finally
        {
            _procesandoImpresionCobro=false;
            ReciboCobroImprimirOriginalButton.IsEnabled=RolesAparcar.Permite(
                _db.SesionActual?.Rol??"",PermisosAparcar.Impresion);
        }
    }

    private void ReciboCobroImprimirCopia_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoImpresionCobro)return;
        _procesandoImpresionCobro=true;
        ReciboCobroImprimirCopiaButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Reimpresion);
            var recibo=ReciboCobroElegidoV21();
            var motivo=ReciboCobroMotivoCopiaInput.Text.Trim();
            if(motivo.Length is <8 or >250)
                throw new ArgumentException("Indicá un motivo de copia de 8 a 250 caracteres.");
            ImprimirCobroSeguro(recibo.Cobro,true,motivo,recibo.Devuelto);
            ReciboCobroMotivoCopiaInput.Text="";
            ActualizarRecibosCobroV21(recibo.Cobro.Id);
        }
        catch(Exception ex)
        {
            ReciboCobroHistorialEstadoText.Text="No se envió COPIA: "+ex.Message;
        }
        finally
        {
            _procesandoImpresionCobro=false;
            ReciboCobroImprimirCopiaButton.IsEnabled=RolesAparcar.Permite(
                _db.SesionActual?.Rol??"",PermisosAparcar.Reimpresion);
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
            ImpresoraEstadoText.Text=ImpresorasCombo.Items.Count==0
                ?"Windows no informa impresoras instaladas. Instalá el controlador y actualizá la lista."
                :string.IsNullOrWhiteSpace(_impresora)
                    ?"Elegí la impresora y el ancho, guardá y enviá una página de prueba."
                    :ImpresorasCombo.SelectedItem is null
                        ?"La impresora guardada no aparece en Windows. Seleccioná otra y guardá."
                        :$"Seleccionada: {_impresora} · {_anchoTicket} mm.";
        }
        catch (Exception ex)
        {
            ImpresoraEstadoText.Text = Mensaje.Text = "No se pudo enumerar las impresoras de Windows: " + ex.Message;
        }
    }

    private bool _procesandoX;

    private void ActualizarTalonariosX()
    {
        _db.ExigirPermiso(PermisosAparcar.Consulta);
        var codigo=(TalonariosXCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        TalonariosXCombo.Items.Clear();
        TalonarioXPreferidoCombo.Items.Clear();
        var preferido=_db.ObtenerTalonarioXParaCobros()?.Codigo;
        foreach(var talonario in _db.ListarTalonariosX())
        {
            var texto=$"{talonario.Codigo} · {talonario.Nombre} · "+
                $"último X {talonario.UltimoNumero:D8}"+(talonario.Habilitado?"":" · SUSPENDIDO");
            var item=new ComboBoxItem{Tag=talonario.Codigo,Content=texto};
            TalonariosXCombo.Items.Add(item);
            if(talonario.Codigo==codigo)TalonariosXCombo.SelectedItem=item;
            if(talonario.Habilitado)
            {
                var opcion=new ComboBoxItem{Tag=talonario.Codigo,Content=texto};
                TalonarioXPreferidoCombo.Items.Add(opcion);
                if(talonario.Codigo==preferido)
                    TalonarioXPreferidoCombo.SelectedItem=opcion;
            }
        }

        CobrosXCombo.Items.Clear();
        foreach(var cobro in _db.ListarCobrosParaX(200))
        {
            CobrosXCombo.Items.Add(new ComboBoxItem
            {
                Tag=cobro.Origen+"|"+cobro.CobroId.ToString("D"),
                Content=$"{cobro.FechaCobroUtc.ToLocalTime():dd/MM HH:mm} · "+
                    $"{cobro.Descripcion} · {Dinero(cobro.ImporteCentavos)}"+
                    (cobro.YaEmitido?" · X YA EMITIDO":"")
            });
        }
        HistorialXCombo.Items.Clear();
        foreach(var ticket in _db.ListarTicketsX(200))
        {
            HistorialXCombo.Items.Add(new ComboBoxItem
            {
                Tag=ticket.Talonario+"|"+ticket.Numero.ToString(CultureInfo.InvariantCulture),
                Content=$"X {ticket.Talonario}-{ticket.Numero:D8} · {ticket.Origen} · "+
                    $"{Dinero(ticket.ImporteCentavos)}"
            });
        }
    }

    private void TalonarioXActualizar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            ActualizarTalonariosX();
            TalonarioXResultadoText.Text="Talonarios y comprobantes X actualizados.";
        }
        catch(Exception ex)
        {
            TalonarioXResultadoText.Text="No se actualizó el talonario X: "+ex.Message;
        }
    }

    private void TalonarioXGuardarPreferido_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var codigo=(TalonarioXPreferidoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new ArgumentException("Elegí un talonario X habilitado.");
            var guardado=_db.ConfigurarTalonarioXParaCobros(codigo);
            TalonarioXResultadoText.Text=
                $"Talonario X {guardado.Codigo} elegido para los cobros. "+
                "La casilla Emitir X está desmarcada por defecto en cada operación.";
        }
        catch(Exception ex)
        {
            TalonarioXResultadoText.Text="No se guardó el talonario X: "+ex.Message;
        }
    }

    /// <summary>
    /// Validar ANTES de registrar el cobro si se solicita emitir X.
    /// Ninguna pantalla debe suponer que el ticket X reemplaza la factura ARCA.
    /// </summary>
    private string? PrepararXAntesDelCobro(bool emitir)
    {
        if(!emitir)return null;
        _db.ExigirPermiso(PermisosAparcar.Cobro);
        _db.ExigirPermiso(PermisosAparcar.Impresion);
        var talonario=_db.ObtenerTalonarioXParaCobros() ??
            throw new InvalidOperationException(
                "Configurá el talonario X predeterminado antes de cobrar, o desmarcá Emitir X.");
        if(!talonario.Habilitado)
            throw new InvalidOperationException("El talonario X predeterminado está suspendido.");
        if(string.IsNullOrWhiteSpace(_impresora))
            throw new InvalidOperationException(
                "Configurá la impresora para el X o desmarcá Emitir X.");
        var impresora=new PrinterSettings{PrinterName=_impresora};
        if(!impresora.IsValid)
            throw new InvalidOperationException(
                "La impresora configurada no está disponible; desmarcá Emitir X o corregí la impresora.");
        return talonario.Codigo;
    }

    /// <summary>El cobro ya está confirmado. No lanzar errores ni repetir pagos.</summary>
    private string EmitirXDespuesDelCobro(string? talonario,
        string origen,Guid cobroId)
    {
        if(talonario is null)return "";
        try
        {
            var resultado=_db.EmitirTicketX(talonario,origen,cobroId);
            var ticket=resultado.Ticket;
            var id=$"X {ticket.Talonario}-{ticket.Numero:D8}";
            if(!resultado.EsNuevo)
                return $"{id} ya estaba emitido. No se imprimió otro ORIGINAL: "+
                    "pedí COPIA autorizada desde el historial X.";
            try
            {
                EnviarTicketXImpresora(ticket,false,"");
                return $"{id} emitido y enviado al controlador Windows "+
                    "(no confirma impresión física; documento no fiscal).";
            }
            catch(Exception ex)
            {
                return $"{id} QUEDÓ EMITIDO; no se pudo confirmar el resultado de la impresión: {ex.Message}. "+
                    "No vuelvas a cobrar ni emitir: revisá la impresora y utilizá una copia autorizada si hace falta.";
            }
        }
        catch(Exception ex)
        {
            return "COBRO CONFIRMADO, pero no se pudo registrar el X: "+
                ex.Message+". Revisá la operación en el historial antes de volver a intentarlo.";
        }
    }

    private void TalonarioXCrear_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var creado=_db.CrearTalonarioX(
                TalonarioXCodigoInput.Text,TalonarioXNombreInput.Text);
            ActualizarTalonariosX();
            TalonariosXCombo.SelectedItem=TalonariosXCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x=>x.Tag?.ToString()==creado.Codigo);
            TalonarioXResultadoText.Text=
                $"Talonario X {creado.Codigo} creado. Numeración independiente y no fiscal.";
        }
        catch(Exception ex)
        {
            TalonarioXResultadoText.Text="No se creó el talonario X: "+ex.Message;
        }
    }

    private string TalonarioXSeleccionado() =>
        (TalonariosXCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
        throw new ArgumentException("Elegí un talonario X.");

    private (string Origen,Guid CobroId) CobroSeleccionadoX()
    {
        var valor=(CobrosXCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
        var partes=valor.Split('|');
        if(partes.Length!=2 || !Guid.TryParse(partes[1],out var cobroId))
            throw new ArgumentException("Seleccioná un cobro confirmado.");
        return (partes[0],cobroId);
    }

    private void EnviarTicketXImpresora(TicketX ticket,bool copia,string motivo)
    {
        Guid intento=Guid.Empty;
        bool enviado=false;
        try
        {
            intento=_db.RegistrarIntentoImpresionX(ticket.Id,copia,motivo,_impresora??"");
            TicketXPrinter.Imprimir(ticket,_impresora??"",_anchoTicket,copia);
            enviado=true;
            _db.RegistrarResultadoImpresionX(intento,true,"Enviado al controlador de impresión Windows");
        }
        catch(Exception ex)
        {
            if(intento!=Guid.Empty && !enviado)
            {
                try {_db.RegistrarResultadoImpresionX(intento,false,ex.Message);}
                catch { }
            }
            if(enviado)
                throw new InvalidOperationException("El trabajo fue enviado al controlador, pero no se pudo actualizar su auditoría. Revisá la impresora antes de pedir una copia: "+ex.Message,ex);
            throw;
        }
    }

    private void TalonarioXEmitir_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoX)return;
        _procesandoX=true;
        TalonarioXEmitirButton.IsEnabled=false;
        try
        {
            var (origen,cobroId)=CobroSeleccionadoX();
            var resultado=_db.EmitirTicketX(TalonarioXSeleccionado(),origen,cobroId);
            if(!resultado.EsNuevo)
            {
                TalonarioXResultadoText.Text=
                    $"El cobro ya tiene X {resultado.Ticket.Talonario}-{resultado.Ticket.Numero:D8}. "+
                    "No se generó otro número ni otro cobro. Solicitá copia autorizada si necesitás imprimir.";
            }
            else
            {
                try
                {
                    EnviarTicketXImpresora(resultado.Ticket,false,"");
                    TalonarioXResultadoText.Text=
                        $"X {resultado.Ticket.Talonario}-{resultado.Ticket.Numero:D8} registrado y "+
                        "enviado a impresora. No es factura ni confirma impresión física.";
                }
                catch(Exception ex)
                {
                    TalonarioXResultadoText.Text=
                        $"X {resultado.Ticket.Talonario}-{resultado.Ticket.Numero:D8} QUEDÓ EMITIDO, "+
                        "pero no se pudo confirmar el resultado de impresión: "+ex.Message+
                        ". No vuelvas a emitir: revisá la impresora y pedí una copia autorizada si hace falta.";
                }
            }
            try {ActualizarTalonariosX();}
            catch(Exception ex){TalonarioXResultadoText.Text+=" No se actualizó la lista: "+ex.Message;}
        }
        catch(Exception ex)
        {
            TalonarioXResultadoText.Text="No se pudo completar o verificar la emisión del X: "+ex.Message+
                ". Revisá el historial antes de reintentar.";
        }
        finally
        {
            _procesandoX=false;
            TalonarioXEmitirButton.IsEnabled=RolesAparcar.Permite(
                _db.SesionActual?.Rol??"",PermisosAparcar.Cobro);
        }
    }

    private void TalonarioXReimprimir_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoX)return;
        _procesandoX=true;
        TalonarioXReimprimirButton.IsEnabled=false;
        try
        {
            var texto=(HistorialXCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
            var partes=texto.Split('|');
            if(partes.Length!=2 ||
               !long.TryParse(partes[1],NumberStyles.None,CultureInfo.InvariantCulture,
                    out long numero))
                throw new ArgumentException("Seleccioná un ticket X del historial.");
            var ticket=_db.ObtenerTicketX(partes[0],numero);
            EnviarTicketXImpresora(ticket,true,TalonarioXMotivoCopiaInput.Text);
            TalonarioXResultadoText.Text=
                $"COPIA X {ticket.Talonario}-{ticket.Numero:D8} enviada a impresora. "+
                "Solicitud de reimpresión registrada para auditoría.";
        }
        catch(Exception ex)
        {
            TalonarioXResultadoText.Text="No se pudo confirmar el resultado de la reimpresión: "+ex.Message+". Revisá la impresora antes de repetirla.";
        }
        finally
        {
            _procesandoX=false;
            TalonarioXReimprimirButton.IsEnabled=RolesAparcar.Permite(
                _db.SesionActual?.Rol??"",PermisosAparcar.Reimpresion);
        }
    }

    private void ControlDocumentalActualizar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var registros=_db.ListarEstadoDocumentalCobros(200);
            ControlDocumentalCobrosList.ItemsSource=registros.Select(c=>
                $"{c.FechaCobroUtc.ToLocalTime():dd/MM/yy HH:mm} · {c.Origen} · "+
                $"{Dinero(c.ImporteCentavos)} · "+
                (c.TieneTicketX?$"X {c.TalonarioX}-{c.NumeroX:D8}":"SIN X")+
                " · "+(c.Devuelto?"DEVUELTO":c.EstadoSeguimiento)+
                " · "+(c.EstadoHomologacion is null?"SIN SOLICITUD ARCA":
                    $"HOMOLOGACIÓN {c.EstadoHomologacion}"+
                    (c.EsHomologacionAutorizada?" (CAE DE PRUEBA)":""))+
                $" · Cobro {c.CobroId:D}").ToList();
            ControlDocumentalEstadoText.Text=
                $"Cobros consultados: {registros.Count}; "+
                $"con X: {registros.Count(c=>c.TieneTicketX)}; "+
                $"devueltos: {registros.Count(c=>c.Devuelto)}; "+
                $"homologación autorizada: {registros.Count(c=>c.EsHomologacionAutorizada)}. "+
                "Los CAE de prueba y los tickets X NO constituyen factura de producción. "+
                "No se verifica facturación externa.";
        }
        catch(Exception ex)
        {
            ControlDocumentalCobrosList.ItemsSource=null;
            ControlDocumentalEstadoText.Text="No se pudo consultar el control documental: "+ex.Message;
        }
    }

    private bool _procesandoFiscal;
    private Guid? _clienteFiscalIdAplicado;
    private int? _clienteFiscalVersionAplicada;
    private string? _clienteFiscalCuitEmisorAplicado;
    private ConstanciaInscripcionArca? _constanciaClienteConsultada;
    private string? _constanciaClienteCuitEmisor;
    private int? _perfilFiscalVersionAplicada;
    private string? _perfilFiscalCuitAplicado;
    private System.Collections.Generic.IReadOnlyList<CondicionIvaReceptorWsfe> _condicionesIvaConsultadas=
        Array.Empty<CondicionIvaReceptorWsfe>();
    private string? _condicionesIvaCuit;
    private int _condicionesIvaTipo;
    private DateTimeOffset _condicionesIvaFechaUtc;

    private static long LeerFiscalImporte(string valor) =>
        string.IsNullOrWhiteSpace(valor)?0:LeerImporte(valor);

    private static DateOnly LeerFechaFiscal(string texto)
    {
        if(!DateOnly.TryParseExact(texto.Trim(),"yyyy-MM-dd",CultureInfo.InvariantCulture,
            DateTimeStyles.None,out var fecha))
            throw new ArgumentException("Usá fecha yyyy-MM-dd.");
        return fecha;
    }

    private void FiscalActualizar_Click(object sender,RoutedEventArgs e)
    {
        RefrescarFiscalHomologacion();
        RefrescarPerfilesFiscales();
        CargarPreferenciaCf();
        RefrescarClientesFiscales();
    }

    private void RefrescarFiscalHomologacion()
    {
        try
        {
            var solicitudes=_db.ListarFacturasHomologacion(150);
            var reservados=solicitudes.Select(x=>(x.Origen,x.CobroId)).ToHashSet();
            FiscalCobroCombo.Items.Clear();
            foreach(var p in _db.ListarPendientesFiscales(150)
                .Where(p=>p.Estado=="PendienteDatosFiscales" &&
                    !reservados.Contains((p.Origen,p.CobroId))))
                FiscalCobroCombo.Items.Add(new ComboBoxItem
                {
                    Tag=p.Origen+"|"+p.CobroId.ToString("D"),
                    Content=$"{p.FechaUtc.ToLocalTime():dd/MM/yy HH:mm} · {p.Origen} · " +
                        $"{Dinero(p.ImporteCentavos)} · {p.CobroId}"
                });
            FiscalSolicitudesCombo.Items.Clear();
            FiscalNcOriginalCombo.Items.Clear();
            FiscalNotasCreditoCombo.Items.Clear();
            foreach(var f in solicitudes)
            {
                FiscalSolicitudesCombo.Items.Add(new ComboBoxItem
                {
                    Tag=f.Id.ToString("D"),
                    Content=$"{f.Estado} · {f.Origen} · PV {f.Comprobante.PuntoVenta} " +
                        $"tipo {f.Comprobante.Tipo} Nº {f.Comprobante.Numero} · " +
                        $"{Dinero((long)(f.Comprobante.Total*100m))}"
                });
                if(f.Estado=="Autorizada" && (f.Comprobante.Tipo is 1 or 6 or 11))
                    FiscalNcOriginalCombo.Items.Add(new ComboBoxItem
                    {
                        Tag=f.Id.ToString("D"),
                        Content=$"Factura {ClaseFiscal(f.Comprobante.Tipo)} · PV {f.Comprobante.PuntoVenta} Nº {f.Comprobante.Numero} · {Dinero((long)(f.Comprobante.Total*100m))}"
                    });
            }
            foreach(var nc in _db.ListarNotasCreditoHomologacion(150))
            {
                var asociado=nc.Comprobante.ComprobantesAsociados?.FirstOrDefault();
                FiscalNotasCreditoCombo.Items.Add(new ComboBoxItem
                {
                    Tag=nc.Id.ToString("D"),
                    Content=$"{nc.Estado} · NC {ClaseFiscal(nc.Comprobante.Tipo)} · PV {nc.Comprobante.PuntoVenta} Nº {nc.Comprobante.Numero} · " +
                        $"original PV {asociado?.PuntoVenta} Nº {asociado?.Numero} · {Dinero((long)(nc.Comprobante.Total*100m))}"
                });
            }
            // No sobrescribir el resultado del CAE o un error de conciliación.
        }
        catch(Exception ex){FiscalResultadoText.Text="Fiscal: "+ex.Message;}
    }

    private (string Origen,Guid CobroId,long Centavos) FiscalCobroElegido()
    {
        var identificador=(FiscalCobroCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
        var partes=identificador.Split('|');
        if(partes.Length!=2 || !Guid.TryParse(partes[1],out var id))
            throw new ArgumentException("Seleccioná un cobro todavía no facturado.");
        var pendiente=_db.ListarPendientesFiscales(1000)
            .SingleOrDefault(x=>x.CobroId==id && x.Origen==partes[0] &&
                x.Estado=="PendienteDatosFiscales")
            ??throw new InvalidOperationException("El cobro ya no está disponible.");
        return(partes[0],id,pendiente.ImporteCentavos);
    }

    private SolicitudFiscalPrueba FiscalSolicitudElegida()
    {
        if(!Guid.TryParse((FiscalSolicitudesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var id))
            throw new ArgumentException("Seleccioná una solicitud fiscal.");
        return _db.ObtenerFacturaHomologacion(id);
    }

    private NotaCreditoFiscalPrueba FiscalNotaCreditoElegida()
    {
        if(!Guid.TryParse((FiscalNotasCreditoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var id))
            throw new ArgumentException("Seleccioná una nota de crédito.");
        return _db.ObtenerNotaCreditoHomologacion(id);
    }

    private static string ClaseFiscal(int tipo) => tipo switch
    {
        1 or 3 => "A",
        6 or 8 => "B",
        11 or 13 => "C",
        _ => "?"
    };

    private void FiscalNcOriginal_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!Guid.TryParse((FiscalNcOriginalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var id))return;
        try
        {
            var original=_db.ObtenerFacturaHomologacion(id);
            FiscalNcPuntoVentaInput.Text=original.Comprobante.PuntoVenta.ToString(CultureInfo.InvariantCulture);
            var tipo=original.Comprobante.Tipo switch {1=>3,6=>8,11=>13,_=>0};
            FiscalNcTipoCombo.SelectedItem=FiscalNcTipoCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x=>x.Tag?.ToString()==tipo.ToString(CultureInfo.InvariantCulture));
            FiscalNcNumeroInput.Text="";
            FiscalNotaCreditoEstadoText.Text=$"Original seleccionada: Factura {ClaseFiscal(original.Comprobante.Tipo)} PV {original.Comprobante.PuntoVenta} Nº {original.Comprobante.Numero}. Ingresá el número que ARCA reserve para la nota.";
        }
        catch(Exception ex){FiscalNotaCreditoEstadoText.Text="No se pudo cargar la factura original: "+ex.Message;}
    }

    private void FiscalPrepararNotaCredito_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!Guid.TryParse((FiscalNcOriginalCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var originalId))
                throw new ArgumentException("Elegí una factura original autorizada.");
            if(!int.TryParse(FiscalNcPuntoVentaInput.Text.Trim(),out var pv) ||
               !long.TryParse(FiscalNcNumeroInput.Text.Trim(),out var numero) || numero<1)
                throw new ArgumentException("Completá punto de venta y número de la nota de crédito.");
            if(!int.TryParse((FiscalNcTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var tipo))
                throw new ArgumentException("Elegí el tipo de nota de crédito.");
            var original=_db.ObtenerFacturaHomologacion(originalId);
            var nota=original.Comprobante with
            {
                PuntoVenta=pv,Tipo=tipo,Numero=numero,
                ComprobantesAsociados=new[]{new ComprobanteAsociadoWsfe(
                    original.Comprobante.PuntoVenta,original.Comprobante.Tipo,original.Comprobante.Numero)}
            };
            var preparada=_db.PrepararNotaCreditoHomologacion(originalId,nota);
            FiscalNotaCreditoEstadoText.Text=$"Nota de crédito preparada: {preparada.Id}. " +
                $"Tipo {tipo}, PV {pv} Nº {numero}, asociada a Factura {ClaseFiscal(original.Comprobante.Tipo)} " +
                $"PV {original.Comprobante.PuntoVenta} Nº {original.Comprobante.Numero}. Todavía no se solicitó CAE.";
        }
        catch(Exception ex){FiscalNotaCreditoEstadoText.Text="No se preparó la nota de crédito: "+ex.Message;}
    }

    private void ValidarCredencialesFiscal()
    {
        if(!File.Exists(ArcaCertificadoInput.Text))
            throw new FileNotFoundException("Elegí un certificado de homologación .pfx válido.");
        if(string.IsNullOrWhiteSpace(ArcaClavePfxInput.Password))
            throw new ArgumentException("Ingresá la contraseña del certificado PFX.");
    }

    private void CargarPreferenciaCf()
    {
        try
        {
            var cuit=ArcaCuitInput.Text.Trim();
            FiscalCfPorDefectoCheck.IsChecked=
                AdaptadorWsfeFacturador.CuitValido(cuit) &&
                _db.ConsumidorFinalPredeterminado(cuit);
            FiscalPreferenciaCfEstadoText.Text=
                AdaptadorWsfeFacturador.CuitValido(cuit)
                ?"Preferencia cargada para CUIT "+cuit+"."
                :"Ingresá un CUIT emisor válido para consultar o guardar la preferencia.";
        }
        catch(Exception ex)
        {
            FiscalPreferenciaCfEstadoText.Text="No se pudo consultar la preferencia: "+ex.Message;
        }
    }

    private void FiscalGuardarPreferenciaCf_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var cuit=ArcaCuitInput.Text.Trim();
            bool activo=FiscalCfPorDefectoCheck.IsChecked==true;
            _db.ConfigurarConsumidorFinalPredeterminado(cuit,activo);
            FiscalPreferenciaCfEstadoText.Text=
                $"CUIT {cuit}: Consumidor Final predeterminado {(activo?"activado":"desactivado")}. " +
                "Las operaciones que requieren receptor identificado nunca se completan como 99/0.";
        }
        catch(Exception ex)
        {
            FiscalPreferenciaCfEstadoText.Text="No se guardó la preferencia: "+ex.Message;
        }
    }

    private bool AplicarConsumidorFinalDesdeCatalogo(bool seleccionManual)
    {
        _db.ExigirPermiso(PermisosAparcar.Configuracion);
        var cuit=ArcaCuitInput.Text.Trim();
        if(_condicionesIvaCuit!=cuit ||
           _condicionesIvaFechaUtc<DateTimeOffset.UtcNow.AddMinutes(-30))
            throw new InvalidOperationException(
                "Consultá condiciones IVA oficiales recientes antes de elegir Consumidor Final.");
        if(!int.TryParse((ArcaCbteTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out int tipo) || tipo!=_condicionesIvaTipo)
            throw new InvalidOperationException("La clase de factura cambió: actualizá el catálogo IVA.");
        var (_,_,centavos)=FiscalCobroElegido();
        var sugerido=ReglasConsumidorFinalV17.Resolver(tipo,centavos,_condicionesIvaConsultadas,
            FiscalIdentificarPorSolicitudClienteCheck.IsChecked==true);
        if(sugerido is null)
        {
            FiscalPreferenciaCfEstadoText.Text=
                "No se aplicó Consumidor Final sin identificar. Revisá la clase, el importe "+
                "y el catálogo IVA oficial. Para operaciones de $10.000.000 o más "+
                "debe informarse documento del receptor. También se exige cuando el cliente "+
                "solicita identificar su operación.";
            return false;
        }
        if(!seleccionManual && !string.IsNullOrWhiteSpace(FiscalDocNumeroInput.Text) &&
            FiscalDocNumeroInput.Text.Trim()!="0")
        {
            FiscalPreferenciaCfEstadoText.Text=
                "Se conserva el documento ya cargado. Usá el botón si querés cambiar a Consumidor Final.";
            return false;
        }
        var tipoItem=FiscalDocTipoCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x=>x.Tag?.ToString()=="99")
            ??throw new InvalidOperationException("No está disponible el documento 99.");
        var ivaItem=FiscalCondicionIvaCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x=>x.Tag?.ToString()==
                sugerido.CondicionIvaId.ToString(CultureInfo.InvariantCulture))
            ??throw new InvalidOperationException(
                "El catálogo mostrado no incluye el código oficial de Consumidor Final.");
        FiscalDocTipoCombo.SelectedItem=tipoItem;
        FiscalDocNumeroInput.Text="0";
        FiscalCondicionIvaCombo.SelectedItem=ivaItem;
        // No reutilizar datos personales del receptor previamente seleccionado.
        FiscalNombreReceptorInput.Text="CONSUMIDOR FINAL";
        FiscalDomicilioReceptorInput.Text="NR";
        FiscalCorreoReceptorInput.Text="";
        _clienteFiscalIdAplicado=null;
        _clienteFiscalVersionAplicada=null;
        _clienteFiscalCuitEmisorAplicado=null;
        _constanciaClienteConsultada=null;
        _constanciaClienteCuitEmisor=null;
        FiscalPreferenciaCfEstadoText.Text=
            "Consumidor Final sin identificar aplicado desde catálogo oficial. "+
            "Podés sustituirlo por datos de un cliente identificado.";
        return true;
    }

    private void FiscalCobroCombo_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_procesandoFiscal || FiscalCobroCombo.SelectedItem is null)return;
        try
        {
            var cuit=ArcaCuitInput.Text.Trim();
            if(AdaptadorWsfeFacturador.CuitValido(cuit) &&
               _db.ConsumidorFinalPredeterminado(cuit) &&
               _condicionesIvaConsultadas.Count>0 &&
               _condicionesIvaCuit==cuit)
                AplicarConsumidorFinalDesdeCatalogo(false);
        }
        catch(Exception ex)
        {
            FiscalPreferenciaCfEstadoText.Text=
                "No se pudo aplicar Consumidor Final por defecto: "+ex.Message;
        }
    }

    private void FiscalAplicarConsumidorFinal_Click(object sender,RoutedEventArgs e)
    {
        try { AplicarConsumidorFinalDesdeCatalogo(true); }
        catch(Exception ex)
        { FiscalPreferenciaCfEstadoText.Text="No se aplicó Consumidor Final: "+ex.Message; }
    }

    private async void FiscalConsultarConstanciaEmisor_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalConsultarConstanciaEmisorButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            ValidarCredencialesFiscal();
            var cuit=ArcaCuitInput.Text.Trim();
            if(!AdaptadorWsfeFacturador.CuitValido(cuit))
                throw new ArgumentException("Ingresá un CUIT emisor válido.");
            using var arca=new ArcaHomologacion();
            var constancia=await arca.ConsultarConstanciaInscripcionAsync(
                ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,cuit,cuit);
            if(ArcaCuitInput.Text.Trim()!=constancia.Cuit)
                throw new InvalidOperationException("Cambió el CUIT emisor durante la consulta.");
            FiscalRazonSocialEmisorInput.Text=constancia.RazonSocial;
            if(!string.IsNullOrWhiteSpace(constancia.DomicilioFiscal))
                FiscalDomicilioEmisorInput.Text=constancia.DomicilioFiscal;
            // No inferir ni sobrescribir IVA, IIBB, nombre de fantasía o inicio de actividades.
            FiscalConstanciaEmisorEstadoText.Text=
                $"Constancia de HOMOLOGACIÓN {constancia.ConsultadaUtc.ToLocalTime():dd/MM/yyyy HH:mm}: "+
                $"{constancia.RazonSocial}; CUIT {constancia.Cuit}. "+
                $"Domicilio fiscal: {constancia.DomicilioFiscal}. "+
                $"Impuestos informados: {string.Join(", ",constancia.ImpuestosInformados)}. "+
                "Revisá domicilio comercial, IVA, Ingresos Brutos e inicio de actividades antes de guardar un perfil.";
        }
        catch(Exception ex)
        {
            FiscalConstanciaEmisorEstadoText.Text="Consulta del emisor no disponible: "+ex.Message;
        }
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalConsultarConstanciaEmisorButton.IsEnabled=
                RolesAparcar.Permite(_db.SesionActual?.Rol??"",PermisosAparcar.Configuracion);
        }
    }

    private async void FiscalConsultarConstancia_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalConsultarConstanciaButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            ValidarCredencialesFiscal();
            var cuitEmisor=ArcaCuitInput.Text.Trim();
            if((FiscalDocTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()!="80")
                throw new ArgumentException("Para constancia de inscripción seleccioná documento CUIT (80).");
            var cuit=FiscalDocNumeroInput.Text.Trim();
            if(!AdaptadorWsfeFacturador.CuitValido(cuit))
                throw new ArgumentException("Ingresá un CUIT válido de 11 dígitos.");
            using var arca=new ArcaHomologacion();
            var constancia=await arca.ConsultarConstanciaInscripcionAsync(
                ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,cuitEmisor,cuit);
            // Confirmar que no se modificó el documento durante la consulta.
            if(FiscalDocNumeroInput.Text.Trim()!=constancia.Cuit ||
               ArcaCuitInput.Text.Trim()!=cuitEmisor ||
               (FiscalDocTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()!="80")
                throw new InvalidOperationException("Cambió el emisor o receptor durante la consulta; no se aplicaron datos.");
            FiscalNombreReceptorInput.Text=constancia.RazonSocial;
            FiscalDomicilioReceptorInput.Text=constancia.DomicilioFiscal;
            _constanciaClienteConsultada=constancia;
            _constanciaClienteCuitEmisor=cuitEmisor;
            _clienteFiscalIdAplicado=null;
            _clienteFiscalVersionAplicada=null;
            _clienteFiscalCuitEmisorAplicado=null;
            FiscalConstanciaEstadoText.Text=
                $"ARCA HOMOLOGACIÓN · {constancia.ConsultadaUtc.ToLocalTime():dd/MM/yyyy HH:mm} · "+
                $"CUIT {constancia.Cuit} · {constancia.RazonSocial} · "+
                $"Estado {constancia.EstadoClave} · "+
                $"Impuestos informados: {string.Join(", ",constancia.ImpuestosInformados)}. "+
                "Comprobá la situación tributaria con el profesional y elegí condición IVA "+
                "desde el catálogo WSFEv1; la consulta de padrón no determina el ID automáticamente.";
        }
        catch(Exception ex)
        {
            FiscalConstanciaEstadoText.Text="Constancia no disponible: "+ex.Message+
                " Los datos del receptor no se modificaron.";
        }
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalConsultarConstanciaButton.IsEnabled=
                RolesAparcar.Permite(_db.SesionActual?.Rol??"",PermisosAparcar.Configuracion);
        }
    }

    private async void FiscalConsultarCondiciones_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalConsultarCondicionesButton.IsEnabled=false;
        // Invalidar el catálogo anterior ANTES de cualquier operación de red.
        _condicionesIvaConsultadas=Array.Empty<CondicionIvaReceptorWsfe>();
        _condicionesIvaCuit=null;
        _condicionesIvaTipo=0;
        FiscalCondicionIvaCombo.Items.Clear();
        FiscalCondicionIvaEstadoText.Text="";
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            ValidarCredencialesFiscal();
            var cuit=ArcaCuitInput.Text.Trim();
            if(!int.TryParse((ArcaCbteTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                    out var tipo) || tipo is not (1 or 6 or 11))
                throw new ArgumentException("Elegí factura A, B o C.");
            string clase=tipo switch {1=>"A",6=>"B",11=>"C",_=>throw new ArgumentException("Clase inválida.")};
            using var arca=new ArcaHomologacion();
            var catalogo=await arca.ConsultarCondicionesIvaReceptorAsync(
                ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,cuit,clase);
            _condicionesIvaConsultadas=catalogo;
            _condicionesIvaCuit=cuit;
            _condicionesIvaTipo=tipo;
            _condicionesIvaFechaUtc=DateTimeOffset.UtcNow;
            foreach(var item in catalogo)
                FiscalCondicionIvaCombo.Items.Add(new ComboBoxItem
                {
                    Tag=item.Id.ToString(CultureInfo.InvariantCulture),
                    Content=$"{item.Id} · {item.Descripcion}"
                });
            FiscalCondicionIvaEstadoText.Text=
                $"Catálogo oficial cargado: {catalogo.Count} condición(es) válidas para factura {clase}. " +
                "Seleccioná una opción; no se determinó automáticamente la condición del cliente.";
            if(_db.ConsumidorFinalPredeterminado(cuit) &&
               FiscalCobroCombo.SelectedItem is not null)
                AplicarConsumidorFinalDesdeCatalogo(false);
        }
        catch(Exception ex)
        {
            FiscalCondicionIvaEstadoText.Text="No se obtuvo un catálogo verificable: "+ex.Message;
        }
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalConsultarCondicionesButton.IsEnabled=
                RolesAparcar.Permite(_db.SesionActual?.Rol??"",PermisosAparcar.Configuracion);
        }
    }

    private async void FiscalPreparar_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalPrepararButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            ValidarCredencialesFiscal();
            var (origen,cobroId,importeCentavos)=FiscalCobroElegido();
            if(!int.TryParse(ArcaPtoVtaInput.Text,out var pv) ||
               !int.TryParse((ArcaCbteTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var tipo))
                throw new ArgumentException("Ingresá punto de venta y tipo de factura.");
            if(!int.TryParse((FiscalDocTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var docTipo) ||
               !long.TryParse(FiscalDocNumeroInput.Text,out var docNumero) ||
               !int.TryParse((FiscalCondicionIvaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var condicion))
                throw new ArgumentException("Completá el documento y elegí una condición IVA consultada en ARCA.");
            if(_condicionesIvaCuit!=ArcaCuitInput.Text.Trim() || _condicionesIvaTipo!=tipo ||
               _condicionesIvaFechaUtc<DateTimeOffset.UtcNow.AddMinutes(-30) ||
               !_condicionesIvaConsultadas.Any(x=>x.Id==condicion))
                throw new InvalidOperationException(
                    "La condición IVA no pertenece a un catálogo reciente del mismo CUIT y clase. Consultá ARCA nuevamente.");
            var neto=LeerFiscalImporte(FiscalNetoInput.Text)/100m;
            var iva=LeerFiscalImporte(FiscalIvaInput.Text)/100m;
            var exento=LeerFiscalImporte(FiscalExentoInput.Text)/100m;
            var noGravado=LeerFiscalImporte(FiscalNoGravadoInput.Text)/100m;
            int alicuota=0;
            var alicuotas=new System.Collections.Generic.List<IvaFiscalWsfe>();
            if(neto>0m)
            {
                if(!int.TryParse(FiscalAlicuotaInput.Text,out alicuota))
                    throw new ArgumentException("Para neto gravado completá código de alícuota IVA oficial.");
                alicuotas.Add(new IvaFiscalWsfe(alicuota,neto,iva));
            }
            var hoy=DateOnly.FromDateTime(DateTime.Now);
            var desde=LeerFechaFiscal(FiscalServicioDesdeInput.Text);
            var hasta=LeerFechaFiscal(FiscalServicioHastaInput.Text);
            var vencimiento=LeerFechaFiscal(FiscalVencimientoInput.Text);
            // Estacionamiento, lavadero y abonos son prestaciones de servicios.
            // Nunca inventar fechas fiscales ni determinar IVA automáticamente.
            using var arca=new ArcaHomologacion();
            var numeroArca=await arca.ConsultarUltimoAutorizadoAsync(
                ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,
                ArcaCuitInput.Text.Trim(),pv,tipo);
            var comprobante=new ComprobanteFiscalWsfe(
                pv,tipo,checked((long)numeroArca.Numero+1),hoy,
                2,docTipo,docNumero,condicion,importeCentavos/100m,
                noGravado,neto,exento,0m,iva,alicuotas,desde,hasta,vencimiento);
            AdaptadorWsfeFacturador.Validar(comprobante);
            var borrador=_db.PrepararFacturaHomologacion(
                ArcaCuitInput.Text.Trim(),origen,cobroId,comprobante);
            FiscalResultadoText.Text=$"Borrador {borrador.Id} preparado: " +
                $"PV {pv} tipo {tipo} Nº {comprobante.Numero}, total {Dinero(importeCentavos)}. " +
                "Todavía NO se solicitó CAE. Revisá todos los datos antes de emitir en homologación.";
            RefrescarFiscalHomologacion();
        }
        catch(Exception ex){FiscalResultadoText.Text="No se preparó factura: "+ex.Message;}
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalPrepararButton.IsEnabled=true;
        }
    }

    private async void FiscalEmitir_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalEmitirButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var borrador=FiscalSolicitudElegida();
            if(borrador.Estado!="Preparada")
                throw new InvalidOperationException("La solicitud no está preparada o ya fue enviada. Usá Conciliar.");
            ValidarCredencialesFiscal();
            if(!string.Equals(ArcaCuitInput.Text.Trim(),borrador.CuitEmisor,StringComparison.Ordinal))
                throw new InvalidOperationException("El CUIT emisor difiere del borrador congelado.");
            var dialogo=new ContentDialog
            {
                Title="Emitir factura de PRUEBA en ARCA Homologación",
                Content=$"CUIT {borrador.CuitEmisor}, PV {borrador.Comprobante.PuntoVenta}, " +
                    $"tipo {borrador.Comprobante.Tipo}, número {borrador.Comprobante.Numero}, " +
                    $"total {borrador.Comprobante.Total:N2}. " +
                    "Esta acción enviará FECAESolicitar a HOMOLOGACIÓN, sin validez fiscal. " +
                    "Si la conexión falla NO se reenviará automáticamente.",
                PrimaryButtonText="Solicitar CAE de prueba",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            _db.MarcarEnvioHomologacion(borrador.Id);
            try
            {
                using var arca=new ArcaHomologacion();
                var respuesta=await arca.SolicitarCaePruebaAsync(
                    ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,
                    borrador.CuitEmisor,borrador.Comprobante);
                var guardado=_db.RegistrarRespuestaHomologacion(borrador.Id,respuesta);
                FiscalResultadoText.Text=$"Homologación: {guardado.Estado}. " +
                    (guardado.Cae is null?respuesta.Detalle:
                        $"CAE de PRUEBA {guardado.Cae}, vto {guardado.Vencimiento:dd/MM/yyyy}. " +
                        "No equivale a una factura comercial.");
            }
            catch(Exception ex)
            {
                FiscalResultadoText.Text="ESTADO POR CONCILIAR: "+ex.Message+
                    " NO REINTENTAR. Consultá FECompConsultar desde Aparcar.";
            }
            RefrescarFiscalHomologacion();
        }
        catch(Exception ex){FiscalResultadoText.Text="No se solicitó CAE: "+ex.Message;}
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalEmitirButton.IsEnabled=true;
        }
    }

    private async void FiscalConciliar_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;
        FiscalConciliarButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var borrador=FiscalSolicitudElegida();
            if(borrador.Estado!="PorConciliar")
                throw new InvalidOperationException("Solo corresponde conciliar solicitudes PorConciliar.");
            ValidarCredencialesFiscal();
            if(!string.Equals(ArcaCuitInput.Text.Trim(),borrador.CuitEmisor,StringComparison.Ordinal))
                throw new InvalidOperationException("El CUIT no coincide con el emisor de la solicitud.");
            using var arca=new ArcaHomologacion();
            var consulta=await arca.ConsultarComprobanteAsync(ArcaCertificadoInput.Text,
                ArcaClavePfxInput.Password,borrador.CuitEmisor,borrador.Comprobante);
            var conciliado=_db.RegistrarConciliacionHomologacion(borrador.Id,consulta);
            FiscalResultadoText.Text=$"Consulta de homologación conciliada: " +
                $"CAE de PRUEBA {conciliado.Cae}, vto {conciliado.Vencimiento:dd/MM/yyyy}.";
            RefrescarFiscalHomologacion();
        }
        catch(Exception ex)
        {
            FiscalResultadoText.Text="No fue posible conciliar: "+ex.Message+
                " No vuelvas a enviar FECAESolicitar; se requiere revisión.";
        }
        finally
        {
            ArcaClavePfxInput.Password="";
            _procesandoFiscal=false;
            FiscalConciliarButton.IsEnabled=true;
        }
    }

    private async void FiscalEmitirNotaCredito_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;FiscalEmitirNotaCreditoButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var nota=FiscalNotaCreditoElegida();
            if(nota.Estado!="Preparada")throw new InvalidOperationException("La nota no está preparada o ya fue enviada. Usá Conciliar NC.");
            ValidarCredencialesFiscal();
            if(!string.Equals(ArcaCuitInput.Text.Trim(),nota.CuitEmisor,StringComparison.Ordinal))
                throw new InvalidOperationException("El CUIT emisor difiere de la nota congelada.");
            var dialogo=new ContentDialog
            {
                Title="Emitir nota de crédito de PRUEBA en ARCA Homologación",
                Content=$"CUIT {nota.CuitEmisor}, PV {nota.Comprobante.PuntoVenta}, tipo {nota.Comprobante.Tipo}, número {nota.Comprobante.Numero}. Esta acción no tiene validez fiscal.",
                PrimaryButtonText="Solicitar CAE de prueba",CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            _db.MarcarEnvioNotaCreditoHomologacion(nota.Id);
            try
            {
                using var arca=new ArcaHomologacion();
                var respuesta=await arca.SolicitarCaePruebaAsync(ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,nota.CuitEmisor,nota.Comprobante);
                var guardado=_db.RegistrarRespuestaNotaCreditoHomologacion(nota.Id,respuesta);
                FiscalNotaCreditoEstadoText.Text=$"Homologación NC: {guardado.Estado}. " +
                    (guardado.Cae is null?respuesta.Detalle:$"CAE de PRUEBA {guardado.Cae}, vto {guardado.Vencimiento:dd/MM/yyyy}. No equivale a una nota comercial.");
            }
            catch(Exception ex){FiscalNotaCreditoEstadoText.Text="NC POR CONCILIAR: "+ex.Message+" NO REINTENTAR. Consultá FECompConsultar.";}
            RefrescarFiscalHomologacion();
        }
        catch(Exception ex){FiscalNotaCreditoEstadoText.Text="No se solicitó CAE para la NC: "+ex.Message;}
        finally{ArcaClavePfxInput.Password="";_procesandoFiscal=false;FiscalEmitirNotaCreditoButton.IsEnabled=true;}
    }

    private async void FiscalConciliarNotaCredito_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoFiscal)return;
        _procesandoFiscal=true;FiscalConciliarNotaCreditoButton.IsEnabled=false;
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var nota=FiscalNotaCreditoElegida();
            if(nota.Estado!="PorConciliar")throw new InvalidOperationException("Solo corresponde conciliar notas PorConciliar.");
            ValidarCredencialesFiscal();
            if(!string.Equals(ArcaCuitInput.Text.Trim(),nota.CuitEmisor,StringComparison.Ordinal))
                throw new InvalidOperationException("El CUIT no coincide con el emisor de la nota.");
            using var arca=new ArcaHomologacion();
            var consulta=await arca.ConsultarComprobanteAsync(ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,nota.CuitEmisor,nota.Comprobante);
            var conciliada=_db.RegistrarConciliacionNotaCreditoHomologacion(nota.Id,consulta);
            FiscalNotaCreditoEstadoText.Text=$"Consulta de NC conciliada: CAE de PRUEBA {conciliada.Cae}, vto {conciliada.Vencimiento:dd/MM/yyyy}.";
            RefrescarFiscalHomologacion();
        }
        catch(Exception ex){FiscalNotaCreditoEstadoText.Text="No fue posible conciliar la NC: "+ex.Message+" No vuelvas a enviar FECAESolicitar; se requiere revisión.";}
        finally{ArcaClavePfxInput.Password="";_procesandoFiscal=false;FiscalConciliarNotaCreditoButton.IsEnabled=true;}
    }

    private EmisorDocumentoFiscal CapturarEmisorFiscal(string cuit) =>
        new(
            FiscalRazonSocialEmisorInput.Text.Trim(),
            FiscalNombreFantasiaEmisorInput.Text.Trim(),
            cuit,
            FiscalDomicilioEmisorInput.Text.Trim(),
            FiscalCondicionIvaEmisorInput.Text.Trim(),
            FiscalIngresosBrutosEmisorInput.Text.Trim(),
            LeerFechaFiscal(FiscalInicioActividadesEmisorInput.Text));

    private void AplicarEmisorFiscal(EmisorDocumentoFiscal emisor)
    {
        FiscalRazonSocialEmisorInput.Text=emisor.RazonSocial;
        FiscalNombreFantasiaEmisorInput.Text=emisor.NombreFantasia;
        FiscalDomicilioEmisorInput.Text=emisor.DomicilioComercial;
        FiscalCondicionIvaEmisorInput.Text=emisor.CondicionIva;
        FiscalIngresosBrutosEmisorInput.Text=emisor.IngresosBrutos;
        FiscalInicioActividadesEmisorInput.Text=
            emisor.InicioActividades.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    }

    private void RefrescarPerfilesFiscales(string? seleccionarCuit=null)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var seleccionado=seleccionarCuit ??
                (FiscalPerfilesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var perfiles=_db.ListarPerfilesFiscales();
            FiscalPerfilesCombo.Items.Clear();
            foreach(var perfil in perfiles)
            {
                var item=new ComboBoxItem
                {
                    Tag=perfil.Cuit,
                    Content=$"{perfil.Cuit} · {perfil.Emisor.RazonSocial} · v{perfil.Version}"
                };
                FiscalPerfilesCombo.Items.Add(item);
                if(perfil.Cuit==seleccionado)FiscalPerfilesCombo.SelectedItem=item;
            }
            FiscalPerfilEstadoText.Text=$"{perfiles.Count} perfil(es) fiscales disponibles. "+
                "Estos datos son declarados por el usuario; no están verificados con padrones ARCA.";
        }
        catch(Exception ex)
        {
            FiscalPerfilEstadoText.Text="No se actualizaron perfiles: "+ex.Message;
        }
    }

    private void FiscalPerfilActualizar_Click(object sender,RoutedEventArgs e) =>
        RefrescarPerfilesFiscales();

    private void FiscalPerfilAplicar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var cuit=(FiscalPerfilesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new ArgumentException("Elegí un perfil fiscal guardado.");
            if(!string.IsNullOrWhiteSpace(ArcaCuitInput.Text) &&
               ArcaCuitInput.Text.Trim()!=cuit)
                throw new InvalidOperationException(
                    "El CUIT en la configuración ARCA es distinto. Revisalo antes de aplicar.");
            if(Guid.TryParse(
                (FiscalSolicitudesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var solicitudId) && _db.ObtenerFacturaHomologacion(solicitudId).CuitEmisor!=cuit)
                throw new InvalidOperationException(
                    "El perfil seleccionado no pertenece al CUIT de esta solicitud fiscal.");
            var perfil=_db.ObtenerPerfilFiscalEmisor(cuit) ??
                throw new InvalidOperationException("No se encontró el perfil fiscal seleccionado.");
            ArcaCuitInput.Text=cuit;
            AplicarEmisorFiscal(perfil.Emisor);
            _perfilFiscalCuitAplicado=cuit;
            _perfilFiscalVersionAplicada=perfil.Version;
            FiscalPerfilEstadoText.Text=$"Perfil {cuit} v{perfil.Version} aplicado al formulario. "+
                "Si cambiás los datos, guardá una nueva versión o volvé a aplicar el perfil.";
        }
        catch(Exception ex)
        {
            FiscalPerfilEstadoText.Text="No se aplicó el perfil: "+ex.Message;
        }
    }

    private void FiscalPerfilGuardar_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var emisor=CapturarEmisorFiscal(ArcaCuitInput.Text.Trim());
            var perfil=_db.GuardarPerfilFiscalEmisor(emisor);
            _perfilFiscalCuitAplicado=perfil.Cuit;
            _perfilFiscalVersionAplicada=perfil.Version;
            RefrescarPerfilesFiscales(perfil.Cuit);
            FiscalPerfilEstadoText.Text=
                $"Perfil fiscal {perfil.Cuit}, versión vigente {perfil.Version}, almacenado. "+
                "Las versiones anteriores se conservan.";
        }
        catch(Exception ex)
        {
            FiscalPerfilEstadoText.Text="No se guardó el perfil: "+ex.Message;
        }
    }

    private void FiscalPerfilHistorial_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var cuit=(FiscalPerfilesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                throw new ArgumentException("Seleccioná un perfil fiscal.");
            var versiones=_db.ListarVersionesPerfilFiscal(cuit,12);
            FiscalPerfilEstadoText.Text="Historial del CUIT "+cuit+": "+
                string.Join(" | ",versiones.Select(x=>
                    $"v{x.Version} ({x.CreadoUtc.ToLocalTime():dd/MM/yyyy HH:mm})"+
                    (x.EsVigente?" VIGENTE":" HISTÓRICA")));
        }
        catch(Exception ex)
        {
            FiscalPerfilEstadoText.Text="No se consultó el historial: "+ex.Message;
        }
    }

    private void RefrescarClientesFiscales(Guid? seleccionar=null)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var cuit=ArcaCuitInput.Text.Trim();
            var buscar=FiscalBuscarClientesInput.Text.Trim();
            if(!AdaptadorWsfeFacturador.CuitValido(cuit))
            {
                FiscalClientesCombo.Items.Clear();
                FiscalClientesEstadoText.Text=
                    "Ingresá un CUIT emisor válido para consultar su cartera de clientes.";
                return;
            }
            var clientes=_db.BuscarClientesFiscales(cuit,buscar,100);
            FiscalClientesCombo.Items.Clear();
            foreach(var cliente in clientes)
            {
                var item=new ComboBoxItem
                {
                    Tag=cliente.Id.ToString("D"),
                    Content=$"{cliente.Receptor.DocumentoNumero} · {cliente.Receptor.Nombre} · "+
                        $"v{cliente.Version}"
                };
                FiscalClientesCombo.Items.Add(item);
                if(cliente.Id==seleccionar)FiscalClientesCombo.SelectedItem=item;
            }
            FiscalClientesEstadoText.Text=$"{clientes.Count} cliente(s) encontrados para CUIT {cuit}. "+
                "Se muestran hasta 100 coincidencias.";
        }
        catch(Exception ex)
        {
            FiscalClientesEstadoText.Text="No se pudo buscar clientes: "+ex.Message;
        }
    }

    private Guid ClienteFiscalElegidoId()
    {
        if(!Guid.TryParse((FiscalClientesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var id))
            throw new ArgumentException("Seleccioná un cliente guardado.");
        return id;
    }

    private ReceptorDocumentoFiscal CapturarReceptorFiscalCliente()
    {
        if(!int.TryParse((FiscalDocTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var tipo) ||
           !long.TryParse(FiscalDocNumeroInput.Text.Trim(),out var numero))
            throw new ArgumentException("Ingresá tipo y número de documento válidos.");
        int iva=0;
        if(int.TryParse((FiscalCondicionIvaCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var idIva) &&
           _condicionesIvaCuit==ArcaCuitInput.Text.Trim() &&
           _condicionesIvaFechaUtc>=DateTimeOffset.UtcNow.AddMinutes(-30) &&
           _condicionesIvaConsultadas.Any(x=>x.Id==idIva && x.Clase==
                (_condicionesIvaTipo==1?"A":_condicionesIvaTipo==6?"B":"C")))
            iva=idIva;
        return new ReceptorDocumentoFiscal(
            FiscalNombreReceptorInput.Text.Trim(),FiscalDomicilioReceptorInput.Text.Trim(),
            tipo,numero,iva,FiscalCorreoReceptorInput.Text.Trim());
    }

    private void FiscalBuscarClientes_Click(object sender,RoutedEventArgs e)=>
        RefrescarClientesFiscales();

    private void FiscalGuardarCliente_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var cuit=ArcaCuitInput.Text.Trim();
            var receptor=CapturarReceptorFiscalCliente();
            var ultima=_constanciaClienteConsultada;
            bool verificada=ultima is not null &&
                _constanciaClienteCuitEmisor==cuit &&
                ultima.Cuit==receptor.DocumentoNumero.ToString(CultureInfo.InvariantCulture) &&
                receptor.DocumentoTipo==80 &&
                ultima.RazonSocial==receptor.Nombre &&
                ultima.DomicilioFiscal==receptor.Domicilio;
            var guardado=_db.GuardarClienteFiscal(cuit,receptor,
                verificada?"ConstanciaHomologacion":"Manual",
                verificada?ultima!.ConsultadaUtc:null);
            _clienteFiscalIdAplicado=guardado.Id;
            _clienteFiscalVersionAplicada=guardado.Version;
            _clienteFiscalCuitEmisorAplicado=cuit;
            RefrescarClientesFiscales(guardado.Id);
            FiscalClientesEstadoText.Text=
                $"Cliente {guardado.Receptor.DocumentoNumero} guardado (v{guardado.Version}). "+
                (verificada?"Origen: constancia de homologación ARCA. ":
                    "Origen: datos revisados manualmente. ")+
                "La versión se conservará en la solicitud fiscal.";
        }
        catch(Exception ex)
        {
            FiscalClientesEstadoText.Text="No se pudo guardar el cliente: "+ex.Message;
        }
    }

    private void FiscalAplicarCliente_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var id=ClienteFiscalElegidoId();
            var cliente=_db.ObtenerClienteFiscal(id) ??
                throw new InvalidOperationException("Cliente no encontrado.");
            if(!cliente.Activo)
                throw new InvalidOperationException("El cliente está desactivado.");
            var cuit=ArcaCuitInput.Text.Trim();
            if(cliente.CuitEmisor!=cuit)
                throw new InvalidOperationException("El cliente no pertenece al CUIT emisor seleccionado.");
            var receptor=cliente.Receptor;
            // Priorizar un cliente identificado por encima del CF predeterminado.
            FiscalIdentificarPorSolicitudClienteCheck.IsChecked=true;
            var item=FiscalDocTipoCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x=>x.Tag?.ToString()==receptor.DocumentoTipo.ToString(
                    CultureInfo.InvariantCulture))
                ??throw new InvalidOperationException("Tipo de documento no disponible.");
            FiscalDocTipoCombo.SelectedItem=item;
            FiscalDocNumeroInput.Text=receptor.DocumentoNumero.ToString(CultureInfo.InvariantCulture);
            FiscalNombreReceptorInput.Text=receptor.Nombre;
            FiscalDomicilioReceptorInput.Text=receptor.Domicilio;
            FiscalCorreoReceptorInput.Text=receptor.Correo??"";
            // Un ID IVA histórico no sustituye la consulta vigente del catálogo de ARCA.
            FiscalCondicionIvaCombo.SelectedItem=null;
            var clase=(ArcaCbteTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if(_condicionesIvaCuit==cuit &&
               _condicionesIvaFechaUtc>=DateTimeOffset.UtcNow.AddMinutes(-30) &&
               clase==_condicionesIvaTipo.ToString(CultureInfo.InvariantCulture))
            {
                var permitido=_condicionesIvaConsultadas.Any(x=>
                    x.Id==receptor.CondicionIvaId);
                if(permitido)
                    FiscalCondicionIvaCombo.SelectedItem=
                        FiscalCondicionIvaCombo.Items.OfType<ComboBoxItem>()
                            .FirstOrDefault(x=>x.Tag?.ToString()==
                                receptor.CondicionIvaId.ToString(CultureInfo.InvariantCulture));
            }
            _constanciaClienteConsultada=null;
            _constanciaClienteCuitEmisor=null;
            _clienteFiscalIdAplicado=id;
            _clienteFiscalVersionAplicada=cliente.Version;
            _clienteFiscalCuitEmisorAplicado=cuit;
            FiscalClientesEstadoText.Text=
                $"Cliente cargado desde cartera: versión {cliente.Version}, "+
                $"origen {cliente.OrigenDatos}. Verificá condición IVA desde catálogo ARCA. "+
                "Si cambiás datos, guardá una nueva versión antes del snapshot fiscal.";
        }
        catch(Exception ex)
        {
            FiscalClientesEstadoText.Text="No se aplicó el cliente: "+ex.Message;
        }
    }

    private void FiscalVersionesCliente_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var versiones=_db.ListarVersionesClienteFiscal(ClienteFiscalElegidoId(),12);
            FiscalClientesEstadoText.Text="Versiones guardadas: "+
                string.Join(" | ",versiones.Select(x=>
                    $"v{x.Version} ({x.ModificadoUtc.ToLocalTime():dd/MM/yyyy HH:mm})"));
        }
        catch(Exception ex)
        {
            FiscalClientesEstadoText.Text="No se consultaron versiones: "+ex.Message;
        }
    }

    private void FiscalDesactivarCliente_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var id=ClienteFiscalElegidoId();
            _db.DesactivarClienteFiscal(id);
            if(_clienteFiscalIdAplicado==id)
            {
                _clienteFiscalIdAplicado=null;
                _clienteFiscalVersionAplicada=null;
                _clienteFiscalCuitEmisorAplicado=null;
            }
            RefrescarClientesFiscales();
            FiscalClientesEstadoText.Text=
                "Cliente desactivado en la cartera. No se alteraron comprobantes históricos.";
        }
        catch(Exception ex)
        {
            FiscalClientesEstadoText.Text="No se desactivó el cliente: "+ex.Message;
        }
    }

    private void FiscalGuardarDatosDocumento_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var solicitud=FiscalSolicitudElegida();
            var documento=solicitud.Comprobante;
            var emisor=CapturarEmisorFiscal(solicitud.CuitEmisor);
            var receptor=new ReceptorDocumentoFiscal(
                FiscalNombreReceptorInput.Text.Trim(),
                FiscalDomicilioReceptorInput.Text.Trim(),
                documento.DocTipo,documento.DocNumero,documento.CondicionIvaReceptorId,
                FiscalCorreoReceptorInput.Text.Trim());
            var datos=new DatosDocumentoFiscalPrueba(emisor,receptor,
                FiscalDescripcionServicioInput.Text.Trim(),
                FiscalCondicionVentaInput.Text.Trim(),
                _perfilFiscalCuitAplicado==solicitud.CuitEmisor?
                    _perfilFiscalVersionAplicada:null,
                _clienteFiscalCuitEmisorAplicado==solicitud.CuitEmisor?
                    _clienteFiscalIdAplicado:null,
                _clienteFiscalCuitEmisorAplicado==solicitud.CuitEmisor?
                    _clienteFiscalVersionAplicada:null);
            _db.RegistrarDatosDocumentoFiscalPrueba(solicitud.Id,datos);
            FiscalResultadoText.Text="Identidad comercial de HOMOLOGACIÓN guardada y congelada "+
                "para esta solicitud. No equivale a emitir una factura.";
        }
        catch(Exception ex)
        {
            FiscalResultadoText.Text="No se guardaron datos fiscales: "+ex.Message;
        }
    }

    private void FiscalGuardarPdf_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var solicitud=FiscalSolicitudElegida();
            var datos=_db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id);
            var vista=PresentacionFiscalHomologacion.Crear(solicitud,datos);
            var destino=DocumentoFiscalHomologacion.GuardarPdf(vista);
            FiscalResultadoText.Text="Copia de HOMOLOGACIÓN SIN VALIDEZ FISCAL guardada en: "+destino+
                ". No entregar al cliente como factura.";
        }
        catch(Exception ex)
        {
            FiscalResultadoText.Text="No se generó la copia de prueba: "+ex.Message;
        }
    }

    private void FiscalImprimir_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var solicitud=FiscalSolicitudElegida();
            var datos=_db.ObtenerDatosDocumentoFiscalPrueba(solicitud.Id);
            var vista=PresentacionFiscalHomologacion.Crear(solicitud,datos);
            DocumentoFiscalHomologacion.Imprimir(vista,_impresora??"",_anchoTicket);
            FiscalResultadoText.Text="Trabajo de impresión de HOMOLOGACIÓN enviado al controlador. "+
                "Esto no confirma impresión física ni constituye factura válida.";
        }
        catch(Exception ex)
        {
            FiscalResultadoText.Text="No se imprimió la copia de prueba: "+ex.Message;
        }
    }

    private async void ArcaProbarConexion_Click(object sender,RoutedEventArgs e)
    {
        _db.ExigirPermiso(PermisosAparcar.Configuracion);
        ArcaProbarConexionButton.IsEnabled=false;
        ArcaResultadoText.Text="Consultando autenticación WSAA y último autorizado WSFEv1 en HOMOLOGACIÓN...";
        try
        {
            if(!int.TryParse(ArcaPtoVtaInput.Text,out var pv) ||
               !int.TryParse((ArcaCbteTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                   out var comprobante))
                throw new ArgumentException("Verificá el punto de venta y tipo de factura.");
            using var servicio=new ArcaHomologacion();
            var dato=await servicio.ConsultarUltimoAutorizadoAsync(
                ArcaCertificadoInput.Text,ArcaClavePfxInput.Password,ArcaCuitInput.Text.Trim(),
                pv,comprobante);
            ArcaResultadoText.Text=$"Consulta de homologación correcta. Último número autorizado: " +
                $"{dato.Numero}. Sesión WSAA vence: {dato.ExpiraSesionUtc.ToLocalTime():dd/MM/yyyy HH:mm}. " +
                "No se emitió una factura ni se solicitó CAE.";
        }
        catch(Exception ex)
        {
            // No escribir CMS, claves, tokens ni contenido XML de los servicios en la UI.
            ArcaResultadoText.Text="La consulta de homologación no se completó: "+ex.Message;
        }
        finally
        {
            ArcaClavePfxInput.Password="";
            ArcaProbarConexionButton.IsEnabled=
                RolesAparcar.Permite(_db.SesionActual?.Rol??"",PermisosAparcar.Configuracion);
        }
    }

    private async void ExaminarRespaldo_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var selector=new FileOpenPicker();
            selector.FileTypeFilter.Add(".db");
            selector.SuggestedStartLocation=PickerLocationId.DocumentsLibrary;
            InitializeWithWindow.Initialize(selector,WindowNative.GetWindowHandle(_hostWindow));
            var archivo=await selector.PickSingleFileAsync();
            if(archivo is not null)RutaRespaldoInput.Text=archivo.Path;
        }
        catch(Exception ex){Mensaje.Text="No se pudo examinar: "+ex.Message;}
    }

    private void VerificarRespaldo_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var verificado=_db.VerificarRespaldo(RutaRespaldoInput.Text);
            EstadoRestauracionText.Text=
                $"Respaldo válido. Esquema v{verificado.VersionEsquema}; " +
                $"{verificado.CantidadEstadias} estadías; " +
                $"{verificado.TamanoBytes:N0} bytes.\nSHA256: {verificado.HashSha256}";
        }
        catch(Exception ex)
        {
            EstadoRestauracionText.Text="RESPALDO RECHAZADO: "+ex.Message;
        }
    }

    private async void PrepararRestauracion_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Restauracion);
            var verificado=_db.VerificarRespaldo(RutaRespaldoInput.Text);
            var confirmar=new ContentDialog{
                Title="Preparar recuperación de la base de datos",
                Content=$"Esta copia contiene {verificado.CantidadEstadias} estadías " +
                    $"(esquema v{verificado.VersionEsquema}). Al reiniciar Aparcar " +
                    "reemplazará los datos actuales por los de esta copia. " +
                    "Se conservará un respaldo de la base sustituida. " +
                    "Esta operación requiere tener la caja cerrada. ¿Continuar?",
                PrimaryButtonText="Preparar recuperación",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await confirmar.ShowAsync()!=ContentDialogResult.Primary)return;
            var preparado=_db.PrepararRestauracion(RutaRespaldoInput.Text);
            EstadoRestauracionText.Text=$"Recuperación preparada (v{preparado.VersionEsquema}). " +
                "Cerrá Aparcar normalmente y volvé a abrirlo para aplicar la copia.";
            Mensaje.Text="Copia validada; no se sustituyeron datos durante esta sesión.";
        }
        catch(Exception ex){EstadoRestauracionText.Text="No se preparó recuperación: "+ex.Message;}
    }

    private void GuardarImpresora_Click(object sender, RoutedEventArgs e)
    {
        var elegida = ImpresorasCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(elegida))
        {
            ImpresoraEstadoText.Text = Mensaje.Text = "Seleccioná una impresora instalada en Windows.";
            return;
        }
        try
        {
            var mm = AnchoTicketCombo.SelectedIndex == 0 ? 58 : 80;
            _db.GuardarPreferencia("Impresora", elegida);
            _db.GuardarPreferencia("PapelMm", mm.ToString());
            _db.GuardarPreferencia("AutoImprimirIngreso",AutoImprimirIngresoCheck.IsChecked == true ? "true" : "false");
            _db.GuardarPreferencia("AutoImprimirCobro",AutoImprimirCobroCheck.IsChecked == true ? "true" : "false");
            _impresora = elegida;
            _anchoTicket = mm;
            _autoImprimirIngreso=AutoImprimirIngresoCheck.IsChecked==true;
            _autoImprimirCobro=AutoImprimirCobroCheck.IsChecked==true;
            Mensaje.Text = $"Impresora configurada: {elegida} · {mm} mm.";
            ImpresoraEstadoText.Text = Mensaje.Text + " Ya podés hacer una página de prueba.";
        }
        catch (Exception ex) { ImpresoraEstadoText.Text = Mensaje.Text = "No se pudo guardar la impresora: " + ex.Message; }
    }

    private void ImprimirTicketSeguro(long numero,bool duplicado=false)
    {
        var ticket = _db.ObtenerTicket(numero);
        var destino = _impresora ?? "";
        try
        {
            TicketPrinter.ImprimirIngreso(ticket,destino,_anchoTicket,duplicado);
        }
        catch(Exception ex)
        {
            try {_db.RegistrarImpresion(ticket.EstadiaId,destino,false,(duplicado?"DUPLICADO: ":"")+ex.Message);}
            catch { }
            Mensaje.Text=$"Ingreso #{numero:D7} GUARDADO, pero no se confirmó el envío a la impresora: {ex.Message}. " +
                "Revisá Configuración > Impresora y no repitas el ingreso.";
            return;
        }
        try
        {
            _db.RegistrarImpresion(ticket.EstadiaId,destino,true,
                duplicado?"DUPLICADO: enviado al controlador":"Enviado al controlador de impresión");
            Mensaje.Text=$"Ingreso #{numero:D7} guardado; ticket enviado a {destino}. Revisá el papel.";
        }
        catch(Exception ex)
        {
            Mensaje.Text=$"Ingreso #{numero:D7} guardado y ticket enviado a {destino}, pero no pudo guardarse la auditoría: {ex.Message}. " +
                "Revisá el papel antes de reimprimir.";
        }
    }

    private void ReimprimirTicket_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(TicketReimpresionInput.Text.Trim(), out var numero) || numero <= 0)
        {
            Mensaje.Text = "Ingresá un número de ticket válido para reimprimir.";
            return;
        }
        try
        {
            _db.AutorizarReimpresion(numero,MotivoReimpresionInput.Text);
            MotivoReimpresionInput.Text="";
            ImprimirTicketSeguro(numero,true);
        }
        catch (Exception ex) { Mensaje.Text = "No se pudo autorizar la reimpresión: " + ex.Message; }
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
        if(SectoresIngresoCombo.Items.Count==1) GuardarIngresoButton.Focus(FocusState.Programmatic);
        else SectoresIngresoCombo.Focus(FocusState.Programmatic);
    }

    private void SectoresIngresoCombo_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter || SectoresIngresoCombo.IsDropDownOpen)return;
        e.Handled=true;
        // La asignación automática de plaza es válida; Tab permite indicar una manualmente.
        GuardarIngresoButton.Focus(FocusState.Programmatic);
    }

    private void SectorInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if(PlazaIngresoInput.IsEnabled)
            PlazaIngresoInput.Focus(FocusState.Programmatic);
        else
            GuardarIngresoButton.Focus(FocusState.Programmatic);
    }

    private void PlazaIngresoInput_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter)return;
        e.Handled=true;
        GuardarIngresoButton.Focus(FocusState.Programmatic);
    }

    private void BusquedaInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Buscar_Click(sender, new RoutedEventArgs());
    }

    private void RootGrid_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(PantallaAcceso.Visibility==Visibility.Visible || _db.SesionActual is null)return;
        if(e.Key==VirtualKey.F2 &&
           RolesAparcar.Permite(_db.SesionActual.Rol,PermisosAparcar.Ingreso))
        {
            if(ReferenceEquals(Navegacion.SelectedItem,Navegacion.MenuItems[1])) Mostrar("ingreso");
            else Navegacion.SelectedItem=Navegacion.MenuItems[1];
        }
        else if(e.Key==VirtualKey.F4 &&
                RolesAparcar.Permite(_db.SesionActual.Rol,PermisosAparcar.Cobro))
        {
            if(ReferenceEquals(Navegacion.SelectedItem,Navegacion.MenuItems[2])) Mostrar("salida");
            else Navegacion.SelectedItem=Navegacion.MenuItems[2];
        }
        else if(e.Key==VirtualKey.F8 &&
                RolesAparcar.Permite(_db.SesionActual.Rol,PermisosAparcar.Consulta))
            Navegacion.SelectedItem=Navegacion.MenuItems[3];
        else if(e.Key==VirtualKey.F5 && InicioPanel.Visibility==Visibility.Visible
                && ReferenceEquals(InicioPanelTabs.SelectedItem,InicioPanelTabs.TabItems[0]))
            InicioBuscar_Click(sender,new RoutedEventArgs());
        else if(e.Key==VirtualKey.F5 && SalidaPanel.Visibility==Visibility.Visible
                && ReferenceEquals(SalidaPanelTabs.SelectedItem,SalidaPanelTabs.TabItems[0]))
            Buscar_Click(sender,new RoutedEventArgs());
        else if(e.Key==VirtualKey.F10 && InicioPanel.Visibility==Visibility.Visible
                && ReferenceEquals(InicioPanelTabs.SelectedItem,InicioPanelTabs.TabItems[0])
                && InicioCobroPanelV23.Visibility==Visibility.Visible
                && InicioConfirmarCobroButton.IsEnabled && !_procesandoInicioV23)
            InicioConfirmarCobro_Click(sender,new RoutedEventArgs());
        else if(e.Key==VirtualKey.F10 && IngresoPanel.Visibility==Visibility.Visible
                && GuardarIngresoButton.IsEnabled && !_guardandoIngreso)
            Ingresar_Click(sender,new RoutedEventArgs());
        else if(e.Key==VirtualKey.F10 && SalidaPanel.Visibility==Visibility.Visible
                && ReferenceEquals(SalidaPanelTabs.SelectedItem,SalidaPanelTabs.TabItems[0])
                && ConfirmarSalidaButton.IsEnabled && !_procesandoSalida)
            ConfirmarSalida_Click(sender,new RoutedEventArgs());
        else return;
        e.Handled=true;
    }

    private void MovimientoManual_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var tipo=(MovimientoTipoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var turno=_db.ObtenerTurnoAbierto() ??
                throw new InvalidOperationException("Abrí primero la caja.");
            var movimiento=_db.RegistrarMovimientoManual(tipo,LeerImporte(MovimientoMontoInput.Text),
                MovimientoMotivoInput.Text,turno.Operador);
            Mensaje.Text=$"{movimiento.Tipo} registrado: {Dinero(movimiento.ImporteCentavos)}.";
            MovimientoMontoInput.Text="";
            MovimientoMotivoInput.Text="";
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="Movimiento no registrado: "+ex.Message; }
    }

    private void CrearPin_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ConfigurarPinSupervisorInicial(PinInicialBox.Password);
            PinInicialBox.Password="";
            Mensaje.Text="PIN inicial configurado. No es un sistema de roles/autenticación.";
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="No se configuró PIN: "+ex.Message; }
    }

    private async void DevolverEfectivo_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var tag=(CobrosDevolucionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if(!Guid.TryParse(tag,out var cobroId))
                throw new ArgumentException("Seleccioná un cobro en efectivo sin devolver.");
            if(_db.ObtenerTurnoAbierto() is null)
                throw new InvalidOperationException("Abrí un turno para registrar una devolución.");
            var dialogo=new ContentDialog
            {
                Title="Confirmar devolución completa",
                Content="Esta operación registra una salida física de efectivo y no podrá revertirse eliminando el historial. ¿Confirmás que se entregará el dinero al cliente?",
                PrimaryButtonText="Registrar devolución",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            var dev=_db.DevolverCobroEfectivo(cobroId,MotivoDevolucionInput.Text,
                AutorizadorInput.Text,PinDevolucionBox.Password);
            PinDevolucionBox.Password="";
            ResultadoDevolucionText.Text=$"Devolución #{dev.Ticket:D7}: {Dinero(dev.ImporteCentavos)} · {dev.Motivo}";
            Mensaje.Text="Devolución registrada y descontada del efectivo esperado.";
            ActualizarCaja();
        }
        catch(Exception ex)
        {
            PinDevolucionBox.Password="";
            Mensaje.Text="No se registró devolución: "+ex.Message;
        }
    }

    private (DateTimeOffset Desde,DateTimeOffset Hasta,DateTime DiaInicio,DateTime DiaFin) FechasReporte()
    {
        var cultura=CultureInfo.GetCultureInfo("es-AR");
        if(!DateTime.TryParseExact(ReporteDesdeInput.Text.Trim(),"dd/MM/yyyy",cultura,
            DateTimeStyles.None,out var inicio) ||
           !DateTime.TryParseExact(ReporteHastaInput.Text.Trim(),"dd/MM/yyyy",cultura,
            DateTimeStyles.None,out var fin))
            throw new ArgumentException("Ingresá fechas dd/MM/aaaa válidas.");
        if(fin.Date<inicio.Date || (fin.Date-inicio.Date).TotalDays>366)
            throw new ArgumentException("El período debe ser de 1 a 367 días.");
        var desde=new DateTimeOffset(inicio.Date,TimeZoneInfo.Local.GetUtcOffset(inicio.Date)).ToUniversalTime();
        var finExclusivo=fin.Date.AddDays(1);
        var hasta=new DateTimeOffset(finExclusivo,TimeZoneInfo.Local.GetUtcOffset(finExclusivo)).ToUniversalTime();
        return(desde,hasta,inicio.Date,fin.Date);
    }

    private void ConsultarReporteActual()
    {
        try
        {
            var rango=FechasReporte();
            var datos=_db.ReporteRecaudacion(rango.Desde,rango.Hasta);
            _reporteActual=datos;
            ReporteTotalesText.Text=$"Cobros {Dinero(datos.CobrosBrutosCentavos)} · " +
                $"Devoluciones {Dinero(datos.DevolucionesCentavos)} · Neto {Dinero(datos.CobrosNetosCentavos)}\n" +
                $"Efectivo neto {Dinero(datos.EfectivoCentavos)} · Electrónicos {Dinero(datos.ElectronicosCentavos)}";
            ReporteFilasList.ItemsSource=datos.Lineas.Select(x=>
                $"{x.FechaUtc.ToLocalTime():dd/MM HH:mm} · {x.Origen} #{x.Ticket:D7} · {x.Patente} · " +
                $"{x.MedioPago} · {Dinero(x.ImporteCentavos)} · {x.Operador}").ToList();
            PendientesFiscalesList.ItemsSource=_db.ListarPendientesFiscales(150)
                .Select(f=>$"{f.FechaUtc.ToLocalTime():dd/MM HH:mm} · {f.Origen} · "+
                    $"{Dinero(f.ImporteCentavos)} · {f.Estado} · Cobro {f.CobroId}")
                .ToList();
            RutaReporteText.Text="";
        }
        catch(Exception ex)
        {
            _reporteActual=null;
            Mensaje.Text="Error en el reporte: "+ex.Message;
        }
    }

    private void ConsultarReporte_Click(object sender,RoutedEventArgs e) => ConsultarReporteActual();


    private static string TextoCombo(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString()??"";

    private static string MedioCombo(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??MediosPago.Efectivo;

    private void RefrescarLavadero()
    {
        try
        {
            _serviciosLavado=_db.ListarServiciosLavado();
            LavServiciosCombo.Items.Clear();
            foreach(var servicio in _serviciosLavado)
                LavServiciosCombo.Items.Add(new ComboBoxItem
                {
                    Content=$"{servicio.TipoVehiculo} · {servicio.Nombre} v{servicio.Version} · {Dinero(servicio.PrecioCentavos)}",
                    Tag=servicio.Id.ToString("D")
                });
            _ordenesLavado=_db.ListarOrdenesLavado();
            LavOrdenesList.ItemsSource=_ordenesLavado.Select(o=>
                $"#{o.Numero:D5} · {o.Patente} · {o.ServicioNombre} · {Dinero(o.PrecioCentavos)}" +
                $" · {o.Estado} · {o.EstadoPago} · {o.Operario}").ToList();
        }
        catch(Exception ex){ Mensaje.Text="Lavadero: "+ex.Message; }
    }

    private void GuardarServicioLavado_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var servicio=_db.GuardarServicioLavado(
                LavServicioNombreInput.Text,TextoCombo(LavServicioTipoCombo),
                LeerImporte(LavServicioPrecioInput.Text));
            Mensaje.Text=$"Servicio {servicio.Nombre} v{servicio.Version} guardado.";
            RefrescarLavadero();
        }
        catch(Exception ex){ Mensaje.Text="No se guardó el servicio: "+ex.Message; }
    }

    private void CrearOrdenLavado_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var sel=LavServiciosCombo.SelectedItem as ComboBoxItem;
            if(!Guid.TryParse(sel?.Tag?.ToString(),out var servicioId))
                throw new ArgumentException("Elegí un servicio.");
            Guid? estadiaId=null;
            if(LavVincularEstadiaCheck.IsChecked==true)
            {
                var activa=_db.BuscarActiva(LavPatenteInput.Text) ??
                    throw new InvalidOperationException("No hay una estadía activa para esa patente.");
                estadiaId=activa.Id;
            }
            var orden=_db.CrearOrdenLavado(servicioId,LavPatenteInput.Text,
                LavOperarioInput.Text,LavObservacionesInput.Text,estadiaId);
            Mensaje.Text=$"Orden de lavado #{orden.Numero:D5} creada sin cobro automático.";
            LavPatenteInput.Text="";
            LavObservacionesInput.Text="";
            RefrescarLavadero();
        }
        catch(Exception ex){ Mensaje.Text="Orden no creada: "+ex.Message; }
    }

    private OrdenLavado OrdenSeleccionada()
    {
        int indice=LavOrdenesList.SelectedIndex;
        if(indice<0 || indice>=_ordenesLavado.Count)
            throw new ArgumentException("Seleccioná una orden de lavado.");
        return _ordenesLavado[indice];
    }

    private void CambiarEstadoLavado_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var orden=OrdenSeleccionada();
            var destino=(LavEstadoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
            var actualizado=_db.CambiarEstadoOrdenLavado(orden.Id,destino,LavOperarioInput.Text);
            Mensaje.Text=$"Orden #{actualizado.Numero:D5}: {actualizado.Estado}.";
            RefrescarLavadero();
        }
        catch(Exception ex){ Mensaje.Text="Estado no modificado: "+ex.Message; }
    }

    private void CobrarLavado_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoCobroExtra)return;
        _procesandoCobroExtra=true;
        CobroComplementario? cobro=null;
        try
        {
            var talonarioX=PrepararXAntesDelCobro(LavEmitirXCheck.IsChecked==true);
            var orden=OrdenSeleccionada();
            var medio=MedioCombo(LavPagoMedioCombo);
            var recibido=medio==MediosPago.Efectivo?
                LeerImporte(LavRecibidoInput.Text):orden.PrecioCentavos;
            cobro=_db.CobrarComplemento("Lavadero",orden.Id,medio,recibido,
                LavReferenciaInput.Text,LavVerificadoCheck.IsChecked==true,Guid.NewGuid());
            // De aquí en adelante el pago está CONFIRMADO: nunca reintentar.
            Mensaje.Text=$"Lavado #{orden.Numero:D5} cobrado: {Dinero(cobro.ImporteCentavos)}."+
                $" Vuelto {Dinero(cobro.VueltoCentavos)}.";
            if(talonarioX is not null)
                Mensaje.Text+=" "+EmitirXDespuesDelCobro(talonarioX,"Lavadero",cobro.Id);
            LavEmitirXCheck.IsChecked=false;
            LavRecibidoInput.Text="";LavReferenciaInput.Text="";LavVerificadoCheck.IsChecked=false;
            try
            {
                RefrescarLavadero();
                ActualizarCaja();
            }
            catch(Exception ex)
            {
                Mensaje.Text+=" El cobro quedó confirmado, pero la pantalla no se actualizó: "+
                    ex.Message+". No cobres de nuevo.";
            }
        }
        catch(Exception ex)
        {
            Mensaje.Text=cobro is null
                ?"No se confirmó el lavado: "+ex.Message
                :"El lavado YA FUE COBRADO. Falló una acción posterior: "+ex.Message+
                    ". No vuelvas a cobrar.";
        }
        finally {_procesandoCobroExtra=false;}
    }

    private void RefrescarAbonados()
    {
        try
        {
            _planesAbono=_db.ListarPlanesAbono();
            AbPlanesCombo.Items.Clear();
            foreach(var plan in _planesAbono)
                AbPlanesCombo.Items.Add(new ComboBoxItem
                {
                    Content=$"{plan.Nombre} · {plan.TipoVehiculo} · {plan.Periodicidad} / {plan.Modalidad} · {Dinero(plan.PrecioCentavos)}",
                    Tag=plan.Id.ToString("D")
                });
            _contratosAbono=_db.ListarContratosAbono();
            AbContratosList.ItemsSource=_contratosAbono.Select(c=>
                $"{c.Cliente} · {c.Patente} · {c.PlanNombre} / {c.Modalidad} · {c.Estado}" +
                (c.FinUtc is null?" · Sin cobertura":$" · Vence {c.FinUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}")).ToList();
        }
        catch(Exception ex){ Mensaje.Text="Abonados: "+ex.Message; }
    }

    private void GuardarPlanAbono_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var plan=_db.GuardarPlanAbono(AbPlanNombreInput.Text,
                TextoCombo(AbPlanTipoCombo),TextoCombo(AbPlanPeriodoCombo),
                LeerImporte(AbPlanPrecioInput.Text),
                (AbPlanModalidadCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()??ModalidadesAbono.Completa);
            Mensaje.Text=$"Plan {plan.Nombre} v{plan.Version} creado.";
            RefrescarAbonados();
        }
        catch(Exception ex){ Mensaje.Text="Plan no guardado: "+ex.Message; }
    }

    private void CrearContratoAbono_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!Guid.TryParse((AbPlanesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var planId))
                throw new ArgumentException("Elegí un plan.");
            var contrato=_db.CrearContratoPendiente(AbClienteInput.Text,AbPatenteInput.Text,planId);
            Mensaje.Text=$"Contrato para {contrato.Patente} creado PENDIENTE. No brinda cobertura hasta ser cobrado.";
            RefrescarAbonados();
        }
        catch(Exception ex){ Mensaje.Text="Contrato no creado: "+ex.Message; }
    }

    private ContratoAbono ContratoSeleccionado()
    {
        int indice=AbContratosList.SelectedIndex;
        if(indice<0 || indice>=_contratosAbono.Count)
            throw new ArgumentException("Seleccioná un contrato.");
        return _contratosAbono[indice];
    }


    private void AbContratosList_SelectionChanged(object sender,SelectionChangedEventArgs e) =>
        ActualizarPatentesContratoSeleccionado();

    private void ActualizarPatentesContratoSeleccionado()
    {
        if(AbVehiculosText is null || AbContratosList.SelectedIndex<0 ||
           AbContratosList.SelectedIndex>=_contratosAbono.Count)return;
        try
        {
            var contrato=ContratoSeleccionado();
            var vehiculos=_db.ListarVehiculosAbono(contrato.Id);
            AbVehiculosText.Text="Patentes autorizadas: "+string.Join(", ",vehiculos.Select(v=>v.Patente));
        }
        catch(Exception ex){AbVehiculosText.Text="No se pudo listar patentes: "+ex.Message;}
    }

    private void AgregarPatenteAbono_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var contrato=ContratoSeleccionado();
            _db.AgregarVehiculoAbono(contrato.Id,AbPatenteExtraInput.Text);
            Mensaje.Text="Patente añadida al contrato pendiente.";
            ActualizarPatentesContratoSeleccionado();
        }
        catch(Exception ex){Mensaje.Text="No se agregó la patente: "+ex.Message;}
    }

    private void QuitarPatenteAbono_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var contrato=ContratoSeleccionado();
            _db.QuitarVehiculoAbono(contrato.Id,AbPatenteExtraInput.Text);
            Mensaje.Text="Patente quitada del contrato pendiente.";
            ActualizarPatentesContratoSeleccionado();
        }
        catch(Exception ex){Mensaje.Text="No se quitó la patente: "+ex.Message;}
    }

    private void RenovarAbono_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var contrato=ContratoSeleccionado();
            if(!Guid.TryParse((AbPlanesCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var planId))
                throw new ArgumentException("Elegí el plan con el que renovarás el contrato.");
            var renovacion=_db.CrearRenovacion(contrato.Id,planId);
            Mensaje.Text=$"Renovación de {contrato.Patente} pendiente de cobro. Número de contrato: {renovacion.Id}.";
            RefrescarAbonados();
        }
        catch(Exception ex){Mensaje.Text="No se creó la renovación: "+ex.Message;}
    }

    private async void DevolverComplemento_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!Guid.TryParse((DevolucionComplementoCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var cobroId))throw new ArgumentException("Elegí un cobro para devolver.");
            var dialogo=new ContentDialog{
                Title="Devolución completa de complemento",
                Content="Esta acción registra una salida de efectivo y cancela el servicio o la cobertura. No se puede deshacer borrando el registro.",
                PrimaryButtonText="Confirmar devolución",
                CloseButtonText="Cancelar",
                DefaultButton=ContentDialogButton.Close,
                XamlRoot=RootGrid.XamlRoot
            };
            if(await dialogo.ShowAsync()!=ContentDialogResult.Primary)return;
            var devolucion=_db.DevolverComplementoEfectivo(cobroId,
                DevolucionComplementoMotivoInput.Text,DevolucionComplementoSupervisorInput.Text,
                DevolucionComplementoPin.Password);
            Mensaje.Text=$"Devolución de {devolucion.Categoria} registrada: {Dinero(devolucion.ImporteCentavos)}.";
            DevolucionComplementoMotivoInput.Text="";
            DevolucionComplementoPin.Password="";
            ActualizarCaja();
        }
        catch(Exception ex)
        {
            DevolucionComplementoPin.Password="";
            Mensaje.Text="Devolución no registrada: "+ex.Message;
        }
    }

    private void CobrarAbono_Click(object sender,RoutedEventArgs e)
    {
        if(_procesandoCobroExtra)return;
        _procesandoCobroExtra=true;
        CobroComplementario? cobro=null;
        try
        {
            var talonarioX=PrepararXAntesDelCobro(AbEmitirXCheck.IsChecked==true);
            var contrato=ContratoSeleccionado();
            var medio=MedioCombo(AbPagoMedioCombo);
            var recibido=medio==MediosPago.Efectivo?
                LeerImporte(AbRecibidoInput.Text):contrato.PrecioCentavos;
            cobro=_db.CobrarComplemento("Abono",contrato.Id,medio,recibido,
                AbReferenciaInput.Text,AbVerificadoCheck.IsChecked==true,Guid.NewGuid());
            Mensaje.Text=$"Abono de {contrato.Patente} activado: {Dinero(cobro.ImporteCentavos)}."+
                $" Vuelto {Dinero(cobro.VueltoCentavos)}. Cobertura desde el pago.";
            if(talonarioX is not null)
                Mensaje.Text+=" "+EmitirXDespuesDelCobro(talonarioX,"Abono",cobro.Id);
            AbEmitirXCheck.IsChecked=false;
            AbRecibidoInput.Text="";AbReferenciaInput.Text="";AbVerificadoCheck.IsChecked=false;
            try
            {
                RefrescarAbonados();
                ActualizarCaja();
            }
            catch(Exception ex)
            {
                Mensaje.Text+=" El cobro quedó confirmado, pero la pantalla no se actualizó: "+
                    ex.Message+". No cobres de nuevo.";
            }
        }
        catch(Exception ex)
        {
            Mensaje.Text=cobro is null
                ?"No se confirmó el abono: "+ex.Message
                :"El abono YA FUE COBRADO. Falló una acción posterior: "+ex.Message+
                    ". No vuelvas a cobrar.";
        }
        finally {_procesandoCobroExtra=false;}
    }

    private void ExportarPdf_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Reportes);
            var rango=FechasReporte();
            var informe=_db.ReporteRecaudacion(rango.Desde,rango.Hasta);
            var destino=ExportadorPdf.Guardar(informe,rango.DiaInicio,rango.DiaFin);
            RutaReporteText.Text="PDF generado: "+destino;
            Mensaje.Text="Reporte PDF generado correctamente.";
        }
        catch(Exception ex)
        {
            Mensaje.Text="No se pudo generar PDF: "+ex.Message;
        }
    }

    private void ExportarExcel_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var fechas=FechasReporte();
            var datos=_db.ReporteRecaudacion(fechas.Desde,fechas.Hasta);
            var archivo=ExportadorExcel.Guardar(datos,fechas.DiaInicio,fechas.DiaFin);
            RutaReporteText.Text="Excel generado: "+archivo;
            Mensaje.Text="Archivo Excel generado correctamente.";
        }
        catch(Exception ex){ Mensaje.Text="No se pudo generar Excel: "+ex.Message; }
    }

    private void Respaldar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ruta = _db.CrearRespaldo();
            RespaldoEstadoText.Text = "Respaldo manual creado: " + ruta;
            Mensaje.Text = "Respaldo creado correctamente.";
        }
        catch (Exception ex) { Mensaje.Text = "Error al respaldar: " + ex.Message; }
    }

    private void GuardarRespaldoPolitica_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var activo = RespaldoAutomaticoInicioCheck.IsChecked == true;
            _db.GuardarPreferencia("RespaldoAutomaticoInicio", activo ? "true" : "false");
            RespaldoEstadoText.Text = activo
                ? "Se creará un respaldo consistente en cada inicio."
                : "Respaldo automático de inicio desactivado; los respaldos manuales siguen disponibles.";
            Mensaje.Text = "Política de respaldos guardada.";
        }
        catch (Exception ex) { RespaldoEstadoText.Text = "No se guardó la política: " + ex.Message; }
    }

    private void ActualizarCierresZ_Click(object sender, RoutedEventArgs e) => ActualizarCierresZ();

    private void ActualizarCierresZ()
    {
        try
        {
            CierresZList.ItemsSource = _db.ListarCierresZ(100)
                .Select(z => $"Z {z.Numero:D8} · {z.CierreUtc.ToLocalTime():dd/MM/yyyy HH:mm} · " +
                    $"{z.Operador} · efectivo {Dinero(z.RecaudacionEfectivoCentavos)} · " +
                    $"contado {Dinero(z.ContadoCentavos)} · diferencia {Dinero(z.DiferenciaCentavos)}")
                .ToList();
            CierresZEstadoText.Text = "Los cierres Z son inmutables y quedan vinculados al turno cerrado.";
        }
        catch (Exception ex) { CierresZEstadoText.Text = "No se pudieron cargar los cierres Z: " + ex.Message; }
    }
}
