namespace Aparcar.Domain;

public static class EstadosLavado
{
    public const string Pendiente = "Pendiente";
    public const string EnProceso = "EnProceso";
    public const string Terminado = "Terminado";
    public const string Entregado = "Entregado";
    public const string Cancelado = "Cancelado";

    public static bool PuedeTransicionar(string origen,string destino) =>
        (origen,destino) switch
        {
            (Pendiente,EnProceso) or (Pendiente,Cancelado) or
            (EnProceso,Terminado) or (Terminado,Entregado) => true,
            _ => false
        };
}

public sealed record ServicioLavado(
    Guid Id,string Nombre,string TipoVehiculo,int Version,long PrecioCentavos,bool Activo);

public sealed record OrdenLavado(
    Guid Id,long Numero,string Patente,string TipoVehiculo,
    Guid ServicioId,string ServicioNombre,long PrecioCentavos,
    Guid? EstadiaId,string Estado,string EstadoPago,
    string Operario,string Observaciones,DateTimeOffset CreadoUtc);

public sealed record CobroComplementario(
    Guid Id,string Categoria,Guid ReferenciaId,string MedioPago,
    long ImporteCentavos,long EntregadoCentavos,long VueltoCentavos,
    DateTimeOffset FechaUtc);
