namespace Aparcar.Domain;

/// <summary>
/// Sector-specific rates apply to the whole stay according to entry time.
/// A future pricing engine may split a stay into multiple time bands.
/// </summary>
public static class FranjasTarifarias
{
    public const string Diurna = "Diurna";
    public const string Nocturna = "Nocturna";
    public static bool Valida(string franja) => franja is Diurna or Nocturna;

    public static string Elegir(TimeOnly horaIngreso,TimeOnly inicioNoche,TimeOnly finNoche)
    {
        if(inicioNoche==finNoche)
            throw new ArgumentException("Inicio y fin de la franja nocturna deben ser distintos.");
        bool noche=inicioNoche<finNoche
            ? horaIngreso>=inicioNoche && horaIngreso<finNoche
            : horaIngreso>=inicioNoche || horaIngreso<finNoche;
        return noche?Nocturna:Diurna;
    }
}

public sealed record HorarioTarifarioSector(
    string Sector,TimeOnly InicioNoche,TimeOnly FinNoche);

public sealed record TarifaSectorVersionada(
    long Id,string Sector,string TipoVehiculo,string Franja,
    int Version,long PrimeraHoraCentavos,long FraccionCentavos,
    int MinutosFraccion,long Tope24HorasCentavos,int CortesiaMinutos)
{
    public TarifaOperativa ComoTarifaOperativa()=>new(
        Id,TipoVehiculo,Version,PrimeraHoraCentavos,FraccionCentavos,
        MinutosFraccion,Tope24HorasCentavos,CortesiaMinutos);
}
