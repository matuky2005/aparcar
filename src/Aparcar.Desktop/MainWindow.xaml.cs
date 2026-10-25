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
    private string? _impresora;
    private int _anchoTicket = 80;
    private CotizacionOperativa? _cotizacion;
    private Guid _claveCobro = Guid.Empty;
    private ResumenRecaudacion? _reporteActual;
    private System.Collections.Generic.List<UsuarioListado> _usuariosActuales = new();
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
    }

    private void MostrarAcceso()
    {
        bool necesitaAdmin=!_db.HayAdministrador();
        PrimerAdministradorPanel.Visibility=necesitaAdmin?Visibility.Visible:Visibility.Collapsed;
        LoginPanel.Visibility=necesitaAdmin?Visibility.Collapsed:Visibility.Visible;
        AccesoMensaje.Text="";
        LoginPasswordBox.Password="";
        AdminInicialPasswordBox.Password="";
        PantallaAcceso.Visibility=Visibility.Visible;
        Navegacion.Visibility=Visibility.Collapsed;
        SalirButton.Visibility=Visibility.Collapsed;
        UsuarioActivoTexto.Text="Sin sesión";
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
        AutoImprimirCheck.IsChecked=_db.ObtenerPreferencia("AutoImprimir")!="false";
        TipoTarifaCombo.SelectedIndex=0;
        ActualizarSectores();
        ActualizarTarifasSector();
        GuardarTarifaSectorButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        GuardarHorarioButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Tarifas);
        var puedeConfigurar=RolesAparcar.Permite(rol,PermisosAparcar.Configuracion);
        ConfigGuardarSectorButton.IsEnabled=puedeConfigurar;
        ConfigGuardarPlazaButton.IsEnabled=puedeConfigurar;
        GuardarPoliticaCajaButton.IsEnabled=puedeConfigurar;
        ArcaProbarConexionButton.IsEnabled=puedeConfigurar;
        FiscalConsultarCondicionesButton.IsEnabled=puedeConfigurar;
        FiscalPrepararButton.IsEnabled=puedeConfigurar;
        FiscalEmitirButton.IsEnabled=puedeConfigurar;
        FiscalConciliarButton.IsEnabled=puedeConfigurar;
        FiscalGuardarPdfButton.IsEnabled=puedeConfigurar;
        FiscalImprimirButton.IsEnabled=puedeConfigurar;
        FiscalGuardarDatosDocumentoButton.IsEnabled=puedeConfigurar;
        CajaCiegaObligatoriaCheck.IsEnabled=puedeConfigurar;
        CajaCiegaObligatoriaCheck.IsChecked=_db.CajaCiegaObligatoria();
        if(_db.CajaCiegaObligatoria())
        {
            CajaCiegaTurnoCheck.IsChecked=true;
            CajaCiegaTurnoCheck.IsEnabled=false;
        }
        ReimprimirTicketButton.IsEnabled=RolesAparcar.Permite(rol,PermisosAparcar.Reimpresion);
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
            PatenteInput.Focus(FocusState.Programmatic);
        }
        if (pantalla == "salida") BusquedaInput.Focus(FocusState.Programmatic);
        if (pantalla == "caja") ActualizarCaja();
        if (pantalla == "tarifas"){MostrarTarifaSeleccionada();ActualizarTarifasSector();}
        if (pantalla == "lavadero") RefrescarLavadero();
        if (pantalla == "abonados") RefrescarAbonados();
        if (pantalla == "reportes") ConsultarReporteActual();
        if (pantalla == "config") ActualizarSectores();
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
        var presentes = _db.ListarActivas();
        TotalActivosText.Text = presentes.Count.ToString();
        var filas = presentes
            .Select(x => $"#{x.Ticket:D7}   {x.Patente,-12}  {x.TipoVehiculo,-11}  {x.IngresoUtc.ToLocalTime():dd/MM HH:mm}")
            .ToList();
        ResumenActivosList.ItemsSource = filas;
        PresentesList.ItemsSource = filas;
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

    private void Ingresar_Click(object sender, RoutedEventArgs e)
    {
        if (_guardandoIngreso) return;
        _guardandoIngreso = true;
        GuardarIngresoButton.IsEnabled = false;
        try
        {
            var tipo = (TipoInput.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var numero = _db.RegistrarIngreso(PatenteInput.Text, tipo, SectorInput.Text,
                string.IsNullOrWhiteSpace(PlazaIngresoInput.Text)?null:PlazaIngresoInput.Text);
            var ticket = _db.ObtenerTicket(numero);
            TicketPreviewText.Text = $"Ticket {ticket.Numero:D7} · {ticket.Patente} · {ticket.TipoVehiculo} · {ticket.Sector}" +
                                    (ticket.Plaza is null?"":$" / plaza {ticket.Plaza}") + " · " +
                                    $"{ticket.FechaIngresoUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
            Mensaje.Text = $"Ingreso guardado. Ticket #{numero:D7}.";
            PatenteInput.Text = "";
            PlazaIngresoInput.Text = "";
            Refrescar();
            ActualizarSectores();
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
                $"\nTarifa versión {_cotizacion.TarifaVersion}" +
                (_cotizacion.CubiertoPorAbono?"\nABONO VIGENTE: cobertura total":"");
            SalidaImporteText.Text = Dinero(_cotizacion.ImporteCentavos);
            CotizacionVigenciaText.Text = "Cotización válida hasta " +
                _cotizacion.VenceUtc.ToLocalTime().ToString("HH:mm:ss");
            ConfirmarSalidaButton.IsEnabled = _cotizacion.TurnoId.HasValue;
            Mensaje.Text = _cotizacion.TurnoId.HasValue
                ? "Contá el efectivo y confirmá el cobro."
                : "Para cobrar primero abrí un turno en Caja.";
            EfectivoRecibidoInput.Text = _cotizacion.CubiertoPorAbono ? "0" : "";
            ReferenciaElectronicaInput.Text = "";
            PagoVerificadoCheck.IsChecked = false;
            VueltoPrevistoText.Text = "";
            if(MedioSeleccionado()==MediosPago.Efectivo)
                EfectivoRecibidoInput.Focus(FocusState.Programmatic);
            else
                ReferenciaElectronicaInput.Focus(FocusState.Programmatic);
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

    private void ConfirmarSalida_Click(object sender, RoutedEventArgs e)
    {
        if (_cotizacion is null || _claveCobro == Guid.Empty) return;
        var medio=MedioSeleccionado();
        long entregado;
        try
        {
            entregado=medio==MediosPago.Efectivo
                ? LeerImporte(EfectivoRecibidoInput.Text)
                : _cotizacion.ImporteCentavos;
        }
        catch(Exception ex){ Mensaje.Text="Importe: "+ex.Message;return; }

        ConfirmarSalidaButton.IsEnabled = false;
        try
        {
            var cobro=_db.ConfirmarPago(_cotizacion,medio,entregado,
                ReferenciaElectronicaInput.Text,PagoVerificadoCheck.IsChecked==true,_claveCobro);
            Mensaje.Text = $"Ticket #{cobro.Ticket:D7} cobrado por {medio}: {Dinero(cobro.ImporteCentavos)}" +
                (medio==MediosPago.Efectivo ? $" · Vuelto {Dinero(cobro.VueltoCentavos)}." : " · Confirmación externa declarada.");
            SalidaDetalleText.Text = "Cobro y salida confirmados. Buscá el siguiente vehículo.";
            SalidaImporteText.Text = "";
            CotizacionVigenciaText.Text = "";
            VueltoPrevistoText.Text = "";
            BusquedaInput.Text = "";
            EfectivoRecibidoInput.Text = "";
            ReferenciaElectronicaInput.Text = "";
            PagoVerificadoCheck.IsChecked = false;
            _cotizacion = null;
            _estadiaSeleccionada = null;
            _claveCobro = Guid.Empty;
            Refrescar();
            ActualizarCaja();
            if (AutoImprimirCheck.IsChecked == true)
                ImprimirCobroSeguro(cobro);
            BusquedaInput.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            Mensaje.Text = "No se confirmó la operación: " + ex.Message +
                " Consultá la estadía y caja antes de reintentar.";
            ConfirmarSalidaButton.IsEnabled = false;
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
                $" · Contado {Dinero(arqueo.ContadoCentavos)} · Diferencia {Dinero(arqueo.DiferenciaCentavos)}.";
            Mensaje.Text="Turno cerrado y arqueo guardado.";
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="No se pudo cerrar caja: "+ex.Message; }
    }

    private void ImprimirCobroSeguro(CobroConfirmadoV4 cobro)
    {
        try
        {
            var recibo=new CobroEfectivoConfirmado(cobro.Id,cobro.EstadiaId,cobro.Ticket,
                cobro.ImporteCentavos,cobro.EntregadoCentavos,cobro.VueltoCentavos,cobro.FechaUtc);
            TicketPrinter.ImprimirCobro(recibo,_impresora ?? "",_anchoTicket,cobro.MedioPago);
            _db.RegistrarImpresion(cobro.EstadiaId,_impresora ?? "",true,
                "Recibo de cobro: enviado al controlador Windows");
            Mensaje.Text += " Recibo no fiscal enviado a impresora.";
        }
        catch (Exception ex)
        {
            try { _db.RegistrarImpresion(cobro.EstadiaId,_impresora ?? "",false,
                    "Error imprimiendo recibo de cobro: "+ex.Message); }
            catch { /* Conservar el error de impresión original. */ }
            Mensaje.Text += " EL COBRO ESTÁ CONFIRMADO, pero falló el recibo: "+ex.Message;
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

    private bool _procesandoFiscal;
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

    private void FiscalActualizar_Click(object sender,RoutedEventArgs e) =>
        RefrescarFiscalHomologacion();

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
            foreach(var f in solicitudes)
                FiscalSolicitudesCombo.Items.Add(new ComboBoxItem
                {
                    Tag=f.Id.ToString("D"),
                    Content=$"{f.Estado} · {f.Origen} · PV {f.Comprobante.PuntoVenta} " +
                        $"tipo {f.Comprobante.Tipo} Nº {f.Comprobante.Numero} · " +
                        $"{Dinero((long)(f.Comprobante.Total*100m))}"
                });
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

    private void ValidarCredencialesFiscal()
    {
        if(!File.Exists(ArcaCertificadoInput.Text))
            throw new FileNotFoundException("Elegí un certificado de homologación .pfx válido.");
        if(string.IsNullOrWhiteSpace(ArcaClavePfxInput.Password))
            throw new ArgumentException("Ingresá la contraseña del certificado PFX.");
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

    private void FiscalGuardarDatosDocumento_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            _db.ExigirPermiso(PermisosAparcar.Configuracion);
            var solicitud=FiscalSolicitudElegida();
            var documento=solicitud.Comprobante;
            var emisor=new EmisorDocumentoFiscal(
                FiscalRazonSocialEmisorInput.Text.Trim(),
                FiscalNombreFantasiaEmisorInput.Text.Trim(),
                solicitud.CuitEmisor,
                FiscalDomicilioEmisorInput.Text.Trim(),
                FiscalCondicionIvaEmisorInput.Text.Trim(),
                FiscalIngresosBrutosEmisorInput.Text.Trim(),
                LeerFechaFiscal(FiscalInicioActividadesEmisorInput.Text));
            var receptor=new ReceptorDocumentoFiscal(
                FiscalNombreReceptorInput.Text.Trim(),
                FiscalDomicilioReceptorInput.Text.Trim(),
                documento.DocTipo,documento.DocNumero,documento.CondicionIvaReceptorId,
                FiscalCorreoReceptorInput.Text.Trim());
            var datos=new DatosDocumentoFiscalPrueba(emisor,receptor,
                FiscalDescripcionServicioInput.Text.Trim(),
                FiscalCondicionVentaInput.Text.Trim());
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

    private void ImprimirTicketSeguro(long numero,bool duplicado=false)
    {
        var ticket = _db.ObtenerTicket(numero);
        var destino = _impresora ?? "";
        try
        {
            TicketPrinter.ImprimirIngreso(ticket, destino, _anchoTicket, duplicado);
            _db.RegistrarImpresion(ticket.EstadiaId, destino, true, duplicado?"DUPLICADO: enviado al controlador":"Enviado al controlador de impresión");
            Mensaje.Text = $"Ingreso #{numero:D7} guardado. Ticket enviado a {destino}.";
        }
        catch (Exception ex)
        {
            // La persistencia del ingreso ya está confirmada; solo se reporta
            // el fallo de impresión, sin iniciar un segundo registro.
            try { _db.RegistrarImpresion(ticket.EstadiaId, destino, false, (duplicado?"DUPLICADO: ":"")+ex.Message); }
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
        SectoresIngresoCombo.Focus(FocusState.Programmatic);
    }

    private void SectoresIngresoCombo_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key!=VirtualKey.Enter || SectoresIngresoCombo.IsDropDownOpen)return;
        e.Handled=true;
        if(PlazaIngresoInput.IsEnabled)
            PlazaIngresoInput.Focus(FocusState.Programmatic);
        else
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
        try
        {
            var orden=OrdenSeleccionada();
            var medio=MedioCombo(LavPagoMedioCombo);
            var recibido=medio==MediosPago.Efectivo?
                LeerImporte(LavRecibidoInput.Text):orden.PrecioCentavos;
            var cobro=_db.CobrarComplemento("Lavadero",orden.Id,medio,recibido,
                LavReferenciaInput.Text,LavVerificadoCheck.IsChecked==true,Guid.NewGuid());
            Mensaje.Text=$"Lavado #{orden.Numero:D5} cobrado: {Dinero(cobro.ImporteCentavos)}."+
                $" Vuelto {Dinero(cobro.VueltoCentavos)}.";
            LavRecibidoInput.Text="";LavReferenciaInput.Text="";LavVerificadoCheck.IsChecked=false;
            RefrescarLavadero();
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="No se confirmó el lavado: "+ex.Message; }
        finally{_procesandoCobroExtra=false;}
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
        try
        {
            var contrato=ContratoSeleccionado();
            var medio=MedioCombo(AbPagoMedioCombo);
            var recibido=medio==MediosPago.Efectivo?
                LeerImporte(AbRecibidoInput.Text):contrato.PrecioCentavos;
            var cobro=_db.CobrarComplemento("Abono",contrato.Id,medio,recibido,
                AbReferenciaInput.Text,AbVerificadoCheck.IsChecked==true,Guid.NewGuid());
            Mensaje.Text=$"Abono de {contrato.Patente} activado: {Dinero(cobro.ImporteCentavos)}." +
                $" Vuelto {Dinero(cobro.VueltoCentavos)}. Cobertura desde el momento del pago.";
            AbRecibidoInput.Text="";AbReferenciaInput.Text="";AbVerificadoCheck.IsChecked=false;
            RefrescarAbonados();
            ActualizarCaja();
        }
        catch(Exception ex){ Mensaje.Text="No se confirmó el abono: "+ex.Message; }
        finally{_procesandoCobroExtra=false;}
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
        try { Mensaje.Text = "Respaldo creado: " + _db.CrearRespaldo(); }
        catch (Exception ex) { Mensaje.Text = "Error al respaldar: " + ex.Message; }
    }
}
