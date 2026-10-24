namespace Aparcar.Domain;

public static class PeriodicidadesAbono
{
    public const string Semanal = "Semanal";
    public const string Quincenal = "Quincenal";
    public const string Mensual = "Mensual";

    public static DateTimeOffset CalcularVencimiento(DateTimeOffset inicio,string periodicidad) =>
        periodicidad switch
        {
            Semanal => inicio.AddDays(7),
            Quincenal => inicio.AddDays(15),
            Mensual => inicio.AddMonths(1),
            _ => throw new ArgumentException("Periodicidad de abono no admitida.")
        };
}

public sealed record PlanAbono(
    Guid Id,string Nombre,string TipoVehiculo,string Periodicidad,
    int Version,long PrecioCentavos,bool Activo,string Modalidad = "Completa");

public sealed record ContratoAbono(
    Guid Id,string Cliente,string Patente,Guid PlanId,string PlanNombre,
    string TipoVehiculo,string Periodicidad,long PrecioCentavos,DateTimeOffset? InicioUtc,
    DateTimeOffset? FinUtc,string Estado,string ReferenciaCobro);

public sealed record CoberturaAbono(Guid ContratoId,string Patente,DateTimeOffset DesdeUtc,
    DateTimeOffset HastaUtc);
