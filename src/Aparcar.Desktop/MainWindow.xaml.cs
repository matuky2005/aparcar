Warning: truncated output (original token count: 42238)
Total output lines: 3574

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
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Aparcar.Desktop;

public sealed partial class MainWindow : Window
{
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

    public MainWindow()
    {
        InitializeComponent();
        Title = "Aparcar — Estacionamientos (demostración)";

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
        SplashOverlay.Visibility = Visibility.Collapsed;
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
            RolesAparcar.Permite(rol,PermisosAparcar.Usuarios)?Visibility.Visible:Visibility.Collapsed;
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
        Refrescar();
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
 …12238 tokens truncated…mite(_db.SesionActual?.Rol??"",PermisosAparcar.Configuracion);
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
            InitializeWithWindow.Initialize(selector,WindowNative.GetWindowHandle(this));
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
