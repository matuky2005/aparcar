using System;
using Aparcar.Domain;
using Xunit;

namespace Aparcar.Tests;

public class TarifasTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(11, 200000)]
    [InlineData(60, 200000)]
    [InlineData(61, 300000)]
    [InlineData(135, 500000)]
    [InlineData(360, 1200000)]
    [InlineData(1440, 1200000)]
    [InlineData(1500, 1400000)]
    [InlineData(2880, 2400000)]
    public void Auto_Calcula_Importe_Simulado(int minutos, long esperado)
    {
        Assert.Equal(esperado, CalculadoraTarifas.CalcularCentavos(
            "Auto", TimeSpan.FromMinutes(minutos)));
    }

    [Fact]
    public void TiempoNegativo_SeRechaza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CalculadoraTarifas.CalcularCentavos("Auto", TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void TipoDesconocido_SeRechaza()
    {
        Assert.Throws<ArgumentException>(() =>
            CalculadoraTarifas.CalcularCentavos("Colectivo", TimeSpan.FromHours(1)));
    }
}
