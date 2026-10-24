namespace Aparcar.Domain;

/// <summary>
/// Precios ficticios y fijos para pruebas. NO utilizar en cobranzas reales.
/// Regla: 10 min de cortesía, primera hora y fracciones de 30 minutos,
/// tope por bloque de 24 horas desde el ingreso. Importes en centavos.
/// </summary>
public static class CalculadoraTarifas
{
    public static long CalcularCentavos(string tipoVehiculo, TimeSpan duracion)
    {
        if (duracion < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duracion));

        long minutos = checked((long)Math.Ceiling(duracion.TotalMinutes));
        var (primeraHora, fraccion, tope) = tipoVehiculo switch
        {
            "Auto" => (200_000L, 100_000L, 1_200_000L),
            "Moto" => (100_000L, 50_000L, 600_000L),
            "Camioneta" => (300_000L, 150_000L, 1_800_000L),
            _ => throw new ArgumentException("Tipo de vehículo desconocido.", nameof(tipoVehiculo))
        };

        if (minutos <= 10) return 0;
        var bloques24h = minutos / 1440;
        var resto = minutos % 1440;
        var valorRestante = resto == 0 ? 0 : resto <= 60
            ? primeraHora
            : checked(primeraHora + ((resto - 60 + 29) / 30) * fraccion);

        return checked(bloques24h * tope + Math.Min(valorRestante, tope));
    }
}
