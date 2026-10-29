using System;
using System.Linq;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class PresentacionFiscalHomologacionTests
{
    private static SolicitudFiscalPrueba Solicitud(string estado="Autorizada",
        string? cae="71234567890123",DateOnly? vencimiento=null)
    {
        var hoy=new DateOnly(2026,10,25);
        var comprobante=new ComprobanteFiscalWsfe(3,6,14,hoy,
            2,96,12345678,5,
            121m,0m,100m,0m,0m,21m,
            new[]{new IvaFiscalWsfe(5,100m,21m)},
            hoy,hoy,hoy);
        return new SolicitudFiscalPrueba(Guid.NewGuid(),"Estacionamiento",
            Guid.NewGuid(),"20123456786",comprobante,estado,cae,
            vencimiento??new DateOnly(2026,11,5),null);
    }

    [Fact]
    public void UnaPruebaAutorizadaExplicitaNoTieneValidezFiscal()
    {
        var vista=PresentacionFiscalHomologacion.Crear(Solicitud());
        var texto=string.Join("\n",vista.Lineas);
        Assert.Contains("PRUEBA SIN VALIDEZ FISCAL",texto);
        Assert.Contains("NO ES FACTURA NI DOCUMENTO TRIBUTARIO",texto);
        Assert.Contains("CAE EXCLUSIVO DE PRUEBA: 71234567890123",texto);
        Assert.Contains("TOTAL: $ 121,00",texto);
        Assert.Contains("Sin codigo QR de validez fiscal",texto);
        Assert.DoesNotContain("https://www.afip.gob.ar/fe/qr/",texto);
    }

    [Theory]
    [InlineData("Preparada")]
    [InlineData("PorConciliar")]
    [InlineData("Rechazada")]
    public void NoPuedeImprimirseUnIntentoNoAutorizado(string estado)
    {
        Assert.Throws<InvalidOperationException>(()=>
            PresentacionFiscalHomologacion.Crear(Solicitud(estado)));
    }

    [Fact]
    public void NoPuedeImprimirseUnaAutorizacionSinCae()
    {
        Assert.Throws<InvalidOperationException>(()=>
            PresentacionFiscalHomologacion.Crear(Solicitud(cae:null)));
    }

    [Fact]
    public void LosDatosDeServicioEImportesSonLosReservados()
    {
        var texto=string.Join("\n",PresentacionFiscalHomologacion.Crear(Solicitud()).Lineas);
        Assert.Contains("Servicio: 25/10/2026 a 25/10/2026",texto);
        Assert.Contains("IVA ID 5: base $ 100,00 / IVA $ 21,00",texto);
        Assert.Contains("Numero: 00000014",texto);
    }
}
