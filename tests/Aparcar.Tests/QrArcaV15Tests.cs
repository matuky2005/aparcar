using System;
using System.Text;
using System.Text.Json;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class QrArcaV15Tests
{
    private static DatosQrArca Ejemplo(AmbienteQrArca ambiente=AmbienteQrArca.Produccion)=>
        new(ambiente,"30000000007",new DateOnly(2026,10,25),
            10,6,94,12100m,96,12345678,"70417054367476");

    [Fact]
    public void NoGeneraCodigoQrParaHomologacion()
    {
        var prueba=Ejemplo(AmbienteQrArca.Homologacion);
        Assert.Throws<InvalidOperationException>(()=>EspecificacionQrArca.CrearUrl(prueba));
    }

    [Fact]
    public void JsonRespetaCamposYTiposOficiales()
    {
        var url=EspecificacionQrArca.CrearUrl(Ejemplo());
        Assert.StartsWith("https://www.arca.gob.ar/fe/qr/?p=",url);
        using var doc=JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(url.Split("?p=")[1])));
        var root=doc.RootElement;
        Assert.Equal(1,root.GetProperty("ver").GetInt32());
        Assert.Equal("2026-10-25",root.GetProperty("fecha").GetString());
        Assert.Equal(30000000007L,root.GetProperty("cuit").GetInt64());
        Assert.Equal(10,root.GetProperty("ptoVta").GetInt32());
        Assert.Equal(6,root.GetProperty("tipoCmp").GetInt32());
        Assert.Equal(94,root.GetProperty("nroCmp").GetInt64());
        Assert.Equal(12100m,root.GetProperty("importe").GetDecimal());
        Assert.Equal("PES",root.GetProperty("moneda").GetString());
        Assert.Equal(1,root.GetProperty("ctz").GetInt32());
        Assert.Equal(96,root.GetProperty("tipoDocRec").GetInt32());
        Assert.Equal(12345678L,root.GetProperty("nroDocRec").GetInt64());
        Assert.Equal("E",root.GetProperty("tipoCodAut").GetString());
        Assert.Equal(70417054367476L,root.GetProperty("codAut").GetInt64());
    }

    [Fact]
    public void OmiteDocumentoAlNoIdentificarConsumidorFinal()
    {
        var url=EspecificacionQrArca.CrearUrl(
            Ejemplo() with {DocumentoTipo=99,DocumentoNumero=0});
        using var doc=JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(url.Split("?p=")[1])));
        Assert.False(doc.RootElement.TryGetProperty("tipoDocRec",out _));
        Assert.False(doc.RootElement.TryGetProperty("nroDocRec",out _));
    }

    [Fact]
    public void NoAceptaCaeRotoNiImportesMasDeDosDecimales()
    {
        Assert.Throws<ArgumentException>(()=>EspecificacionQrArca.CrearUrl(
            Ejemplo() with {Cae="123"}));
        Assert.Throws<ArgumentException>(()=>EspecificacionQrArca.CrearUrl(
            Ejemplo() with {Total=1.234m}));
    }
}
