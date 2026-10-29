using System;
using Aparcar.Domain;
using Xunit;

namespace Aparcar.Tests;

public sealed class PoliticaCobroRapidoV23Tests
{
    [Fact]
    public void Efectivo_CalculaVueltoSinModificarImporteCotizado()
    {
        var datos=PoliticaCobroRapidoV23.Validar(
            170_050,MediosPago.Efectivo,200_000,"",false);
        Assert.Equal(MediosPago.Efectivo,datos.MedioPago);
        Assert.Equal(200_000,datos.EntregadoCentavos);
        Assert.Equal(29_950,datos.VueltoEstimadoCentavos);
    }

    [Fact]
    public void Efectivo_InsuficienteNuncaAutorizaCobro()
    {
        Assert.Throws<InvalidOperationException>(()=>
            PoliticaCobroRapidoV23.Validar(
                25_000,MediosPago.Efectivo,24_999,"",false));
        Assert.Throws<ArgumentOutOfRangeException>(()=>
            PoliticaCobroRapidoV23.Validar(
                25_000,MediosPago.Efectivo,-1,"",false));
    }

    [Theory]
    [InlineData(MediosPago.Debito)]
    [InlineData(MediosPago.Credito)]
    [InlineData(MediosPago.Transferencia)]
    [InlineData(MediosPago.Qr)]
    public void PagoElectronico_SiempreExigeReferenciaYVerificacionExplicita(string medio)
    {
        Assert.Throws<InvalidOperationException>(()=>
            PoliticaCobroRapidoV23.Validar(80_000,medio,80_000,"COD-999",false));
        Assert.Throws<ArgumentException>(()=>
            PoliticaCobroRapidoV23.Validar(80_000,medio,80_000,"XXX",true));
        var datos=PoliticaCobroRapidoV23.Validar(80_000,medio,80_000,"COD-999",true);
        Assert.Equal(0,datos.VueltoEstimadoCentavos);
        Assert.Equal(80_000,datos.EntregadoCentavos);
    }

    [Fact]
    public void PagoElectronico_NoAceptaMontoParcialNiSuperior()
    {
        Assert.Throws<InvalidOperationException>(()=>
            PoliticaCobroRapidoV23.Validar(
                80_000,MediosPago.Transferencia,79_999,"REF-999",true));
        Assert.Throws<InvalidOperationException>(()=>
            PoliticaCobroRapidoV23.Validar(
                80_000,MediosPago.Qr,80_001,"REF-999",true));
    }

    [Fact]
    public void AbonoCubierto_CashCeroEsValido_PeroNoSeAutoVerificaTarjeta()
    {
        var datos=PoliticaCobroRapidoV23.Validar(0,MediosPago.Efectivo,0,"",false);
        Assert.Equal(0,datos.VueltoEstimadoCentavos);
        Assert.Throws<InvalidOperationException>(()=>
            PoliticaCobroRapidoV23.Validar(0,MediosPago.Credito,0,"REF-123",false));
    }

    [Fact]
    public void RechazaMedioDesconocidoEImporteNegativo()
    {
        Assert.Throws<ArgumentException>(()=>
            PoliticaCobroRapidoV23.Validar(1,"Cripto",1,"",true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>
            PoliticaCobroRapidoV23.Validar(-1,MediosPago.Efectivo,1,"",false));
    }
}
