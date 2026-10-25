using System;
using System.Linq;
using System.Xml.Linq;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class WsfeFacturadorReutilizadoTests
{
    private static ComprobanteFiscalWsfe FacturaB() => new(
        PuntoVenta:3,Tipo:6,Numero:128,Fecha:new DateOnly(2026,10,25),
        Concepto:2,DocTipo:96,DocNumero:12345678,CondicionIvaReceptorId:5,
        Total:121m,NoGravado:0m,Neto:100m,Exento:0m,Tributos:0m,Iva:21m,
        Alicuotas:new[]{new IvaFiscalWsfe(5,100m,21m)},
        ServicioDesde:new DateOnly(2026,10,25),
        ServicioHasta:new DateOnly(2026,10,25),
        VencimientoPago:new DateOnly(2026,10,25));

    private const string Cabecera="<FeCabResp><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo></FeCabResp>";

    [Fact]
    public void ConstruyeFeCaeReqDeServicioConImportesYTiemposExactos()
    {
        XElement request=AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB());
        string xml=request.ToString();
        Assert.Contains("FeCAEReq",xml);
        Assert.Contains("<",xml);
        Assert.Equal("3",request.Descendants().Single(x=>x.Name.LocalName=="PtoVta").Value);
        Assert.Equal("128",request.Descendants().Single(x=>x.Name.LocalName=="CbteDesde").Value);
        Assert.Equal("20261025",request.Descendants().Single(x=>x.Name.LocalName=="FchServDesde").Value);
        Assert.Equal("121.00",request.Descendants().Single(x=>x.Name.LocalName=="ImpTotal").Value);
        Assert.Equal("100.00",request.Descendants().Single(x=>x.Name.LocalName=="BaseImp").Value);
        Assert.Equal("21.00",request.Descendants().Single(x=>x.Name.LocalName=="ImpIVA").Value);
        Assert.Equal("5",request.Descendants().Single(x=>x.Name.LocalName=="CondicionIVAReceptorId").Value);
    }

    [Fact]
    public void RechazaFacturasInconsistentesConReglasDeFacturador()
    {
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{Total=120m}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{Tipo=1,DocTipo=99,DocNumero=0}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{Iva=20m}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{CondicionIvaReceptorId=0}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{ServicioHasta=null}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(FacturaB() with{Tipo=11}));
    }

    [Fact]
    public void ParserSoloConsideraAutorizadoConIdentidadYCaeValido()
    {
        string ok="<FECAESolicitarResponse>"+Cabecera+
                  "<FECAEDetResponse><CbteDesde>128</CbteDesde><CbteHasta>128</CbteHasta>"+
                  "<Resultado>A</Resultado><CAE>71234567890123</CAE><CAEFchVto>20261104</CAEFchVto>"+
                  "</FECAEDetResponse></FECAESolicitarResponse>";
        var bueno=AdaptadorWsfeFacturador.InterpretarAutorizacion(ok,FacturaB());
        Assert.Equal(EstadoAutorizacionWsfe.Autorizado,bueno.Estado);
        Assert.Equal("71234567890123",bueno.Cae);
        var distinto=AdaptadorWsfeFacturador.InterpretarAutorizacion(
            ok.Replace("<PtoVta>3</PtoVta>","<PtoVta>4</PtoVta>"),FacturaB());
        Assert.Equal(EstadoAutorizacionWsfe.Indeterminado,distinto.Estado);
        var sinCae=AdaptadorWsfeFacturador.InterpretarAutorizacion(
            ok.Replace("71234567890123",""),FacturaB());
        Assert.Equal(EstadoAutorizacionWsfe.Indeterminado,sinCae.Estado);
        var rechazado=AdaptadorWsfeFacturador.InterpretarAutorizacion(
            ok.Replace("<Resultado>A</Resultado>","<Resultado>R</Resultado>"),FacturaB());
        Assert.Equal(EstadoAutorizacionWsfe.Rechazado,rechazado.Estado);
    }

    [Fact]
    public void ConsultaComparaComprobanteYNoAsumeLibreUnNumeroAnteError()
    {
        string consulta="<FECompConsultarResponse><ResultGet>"+
            "<PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><CbteNro>128</CbteNro>"+
            "<Concepto>2</Concepto><DocTipo>96</DocTipo><DocNro>12345678</DocNro>"+
            "<CondicionIVAReceptorId>5</CondicionIVAReceptorId><CbteFch>20261025</CbteFch>"+
            "<ImpTotal>121.00</ImpTotal><ImpTotConc>0.00</ImpTotConc>"+
            "<ImpNeto>100.00</ImpNeto><ImpOpEx>0.00</ImpOpEx><ImpTrib>0.00</ImpTrib><ImpIVA>21.00</ImpIVA>"+
            "<Iva><AlicIva><Id>5</Id><BaseImp>100.00</BaseImp><Importe>21.00</Importe></AlicIva></Iva>"+
            "<MonId>PES</MonId><MonCotiz>1.00</MonCotiz>"+
            "<FchServDesde>20261025</FchServDesde><FchServHasta>20261025</FchServHasta>"+
            "<FchVtoPago>20261025</FchVtoPago>"+
            "<CodAutorizacion>71234567890123</CodAutorizacion><FchVto>20261104</FchVto>"+
            "</ResultGet></FECompConsultarResponse>";
        var bien=AdaptadorWsfeFacturador.InterpretarConsulta(consulta,FacturaB());
        Assert.True(bien.Encontrado);
        Assert.True(bien.Coincide);
        Assert.True(bien.TieneCae);
        var distinto=AdaptadorWsfeFacturador.InterpretarConsulta(
            consulta.Replace("<ImpTotal>121.00</ImpTotal>","<ImpTotal>999.00</ImpTotal>"),FacturaB());
        Assert.True(distinto.Encontrado);
        Assert.False(distinto.Coincide);
        Assert.False(distinto.TieneCae);
        var condicionEquivocada=AdaptadorWsfeFacturador.InterpretarConsulta(
            consulta.Replace("<CondicionIVAReceptorId>5</CondicionIVAReceptorId>",
                "<CondicionIVAReceptorId>6</CondicionIVAReceptorId>"),FacturaB());
        Assert.False(condicionEquivocada.Coincide);
        var otraAlicuota=AdaptadorWsfeFacturador.InterpretarConsulta(
            consulta.Replace("<Id>5</Id><BaseImp>100.00</BaseImp>",
                "<Id>4</Id><BaseImp>100.00</BaseImp>"),FacturaB());
        Assert.True(otraAlicuota.Encontrado);
        Assert.False(otraAlicuota.Coincide);
        Assert.False(otraAlicuota.TieneCae);
        var baseIncorrecta=AdaptadorWsfeFacturador.InterpretarConsulta(
            consulta.Replace("<BaseImp>100.00</BaseImp>","<BaseImp>95.00</BaseImp>"),FacturaB());
        Assert.False(baseIncorrecta.Coincide);
        var sinDetalleIva=AdaptadorWsfeFacturador.InterpretarConsulta(
            consulta.Replace("<Iva><AlicIva><Id>5</Id><BaseImp>100.00</BaseImp><Importe>21.00</Importe></AlicIva></Iva>",""),FacturaB());
        Assert.False(sinDetalleIva.Coincide);
        var error=AdaptadorWsfeFacturador.InterpretarConsulta(
            "<FECompConsultarResponse><Errors><Err><Code>10001</Code></Err></Errors></FECompConsultarResponse>",
            FacturaB());
        Assert.False(error.Encontrado);
        Assert.Contains("no asumir",error.Detalle);
    }

    [Fact]
    public void FacturaBConsumidorFinalRequiereIdentificacionFiscalExplicita()
    {
        var cf=FacturaB() with{DocTipo=99,DocNumero=0};
        Assert.NotNull(AdaptadorWsfeFacturador.PrepararFeCaeReq(cf));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(cf with{Tipo=11}));
        Assert.Throws<InvalidOperationException>(()=>
            AdaptadorWsfeFacturador.PrepararFeCaeReq(cf with{DocNumero=1}));
    }
}
