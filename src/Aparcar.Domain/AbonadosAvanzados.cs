namespace Aparcar.Domain;

public static class ModalidadesAbono
{
    public const string Completa = "Completa";
    public const string Nocturna = "Nocturna";

    public static bool EsValida(string modalidad) =>
        modalidad is Completa or Nocturna;

    /// <summary>
    /// Modo nocturno: intervalo continuo 20:00-08:00 en la zona horaria
    /// local de la sucursal. No cubre permanencias divididas entre noches.
    /// </summary>
    public static bool CubreHorario(string modalidad,DateTimeOffset ingreso,DateTimeOffset salida,
        TimeZoneInfo? zona=null)
    {
        if(!EsValida(modalidad))throw new ArgumentException("Modalidad inválida.");
        if(salida<ingreso)return false;
        if(modalidad==Completa)return true;
        zona ??=TimeZoneInfo.Local;
        var desde=TimeZoneInfo.ConvertTime(ingreso,zona).DateTime;
        var hasta=TimeZoneInfo.ConvertTime(salida,zona).DateTime;
        DateTime noche=desde.Date.AddHours(20);
        if(desde.Hour<20)noche=noche.AddDays(-1);
        return desde>=noche && hasta<=noche.AddHours(12);
    }
}

public sealed record VehiculoAbonado(Guid ContratoId,string Patente,string TipoVehiculo);
public sealed record DevolucionComplementaria(Guid Id,Guid CobroId,
    Guid TurnoId,string Categoria,Guid ReferenciaId,long ImporteCentavos,
    DateTimeOffset FechaUtc,string Motivo,string AutorizadoPor);
