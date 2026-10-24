namespace Aparcar.Domain;

public static class PermisosAparcar
{
    public const string Ingreso = "Vehiculos.Ingresar";
    public const string Consulta = "Vehiculos.Consultar";
    public const string SalidaPrueba = "Vehiculos.SalidaPrueba";
    public const string Cobro = "Caja.Cobrar";
    public const string AbrirCaja = "Caja.Abrir";
    public const string CerrarCaja = "Caja.Cerrar";
    public const string MovimientosCaja = "Caja.Movimientos";
    public const string Devolucion = "Caja.Devolver";
    public const string Tarifas = "Tarifas.Editar";
    public const string Reportes = "Reportes.Consultar";
    public const string Impresion = "Tickets.Imprimir";
    public const string Usuarios = "Seguridad.Usuarios";
    public const string Auditoria = "Seguridad.Auditoria";
    public const string Respaldo = "Seguridad.Respaldo";
}

public static class RolesAparcar
{
    public const string Administrador = "Administrador";
    public const string Supervisor = "Supervisor";
    public const string Cajero = "Cajero";
    public const string Lavadero = "Lavadero";
    public const string Consulta = "Consulta";

    public static bool Valido(string rol) => rol is Administrador or Supervisor
        or Cajero or Lavadero or Consulta;

    public static bool Permite(string rol,string permiso) =>
        rol == Administrador ||
        rol == Supervisor && permiso is
            (PermisosAparcar.Ingreso or PermisosAparcar.Consulta or
             PermisosAparcar.Cobro or PermisosAparcar.AbrirCaja or
             PermisosAparcar.CerrarCaja or PermisosAparcar.MovimientosCaja or
             PermisosAparcar.Devolucion or PermisosAparcar.Reportes or
             PermisosAparcar.Impresion or PermisosAparcar.Respaldo or
             PermisosAparcar.Auditoria) ||
        rol == Cajero && permiso is
            (PermisosAparcar.Ingreso or PermisosAparcar.Consulta or
             PermisosAparcar.Cobro or PermisosAparcar.AbrirCaja or
             PermisosAparcar.CerrarCaja or PermisosAparcar.Reportes or
             PermisosAparcar.Impresion) ||
        rol == Lavadero && permiso == PermisosAparcar.Consulta ||
        rol == Consulta && permiso is
            (PermisosAparcar.Consulta or PermisosAparcar.Reportes);
}

public sealed record UsuarioSesion(
    Guid Id, string Usuario, string Rol, DateTimeOffset IngresoUtc);

public sealed record UsuarioListado(
    Guid Id,string Nombre,string Rol,bool Activo,DateTimeOffset CreadoUtc);

public sealed record AuditoriaRegistro(
    long Secuencia,DateTimeOffset FechaUtc,string Actor,string Accion,
    string Objeto,string Detalle,string Hash);
