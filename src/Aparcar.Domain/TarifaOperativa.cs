namespace Aparcar.Domain;

/// <summary>Versión inmutable que queda asociada a la estadía al ingresar.</summary>
public sealed record TarifaOperativa(
    long Id, string TipoVehiculo, int Version, long PrimeraHoraCentavos,
    long FraccionCentavos, int MinutosFraccion, long Tope24HorasCentavos,
    int CortesiaMinutos);

public static class MotorTarifas
{
    public static void Validar(TarifaOperativa tarifa)
    {
        if (tarifa.PrimeraHoraCentavos < 0 || tarifa.FraccionCentavos < 0 ||
            tarifa.Tope24HorasCentavos < 0 || tarifa.MinutosFraccion is < 1 or > 1440 ||
            tarifa.CortesiaMinutos is < 0 or > 60)
            throw new ArgumentException("Parámetros de tarifa inválidos.");
        if (tarifa.Tope24HorasCentavos < tarifa.PrimeraHoraCentavos)
            throw new ArgumentException("El máximo diario no puede ser inferior a la primera hora.");
    }

    public static long CalcularCentavos(TarifaOperativa tarifa, TimeSpan duracion)
    {
        Validar(tarifa);
        if (duracion < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duracion));
        long minutos = checked((long)Math.Ceiling(duracion.TotalMinutes));
        if (minutos <= tarifa.CortesiaMinutos) return 0;
        long bloques24h = minutos / 1440;
        long resto = minutos % 1440;
        long parcial = resto == 0 ? 0 : resto <= 60
            ? tarifa.PrimeraHoraCentavos
            : checked(tarifa.PrimeraHoraCentavos +
                ((resto - 60 + tarifa.MinutosFraccion - 1) / tarifa.MinutosFraccion) * tarifa.FraccionCentavos);
        return checked(bloques24h * tarifa.Tope24HorasCentavos +
            Math.Min(parcial, tarifa.Tope24HorasCentavos));
    }
}
