using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class ArcaConsultaTests
{
    private sealed class RespuestasFicticias : HttpMessageHandler
    {
        public int ConsultasWsaa {get;private set;}
        public int ConsultasWsfe {get;private set;}
        public bool DevolverError {get;set;}

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage solicitud,CancellationToken cancelacion)
        {
            string cuerpo=await solicitud.Content!.ReadAsStringAsync(cancelacion);
            if(solicitud.RequestUri==ArcaHomologacion.WsaaUrl)
            {
                ConsultasWsaa++;
                Assert.Contains("loginCms",cuerpo);
                Assert.Contains("in0",cuerpo);
                var doc=System.Xml.Linq.XDocument.Parse(cuerpo);
                var b64=doc.Descendants().First(x=>x.Name.LocalName=="in0").Value;
                var firmado=new SignedCms();
                firmado.Decode(Convert.FromBase64String(b64));
                firmado.CheckSignature(verifySignatureOnly:true);
                Assert.Contains("<service>wsfe</service>",
                    Encoding.UTF8.GetString(firmado.ContentInfo.Content));
                var acceso=new System.Xml.Linq.XElement("loginTicketResponse",
                    new System.Xml.Linq.XElement("header",
                        new System.Xml.Linq.XElement("expirationTime",
                            DateTimeOffset.UtcNow.AddHours(1).ToString("O"))),
                    new System.Xml.Linq.XElement("credentials",
                        new System.Xml.Linq.XElement("token","TOKEN_SIMULADO"),
                        new System.Xml.Linq.XElement("sign","SIGN_SIMULADO"))).ToString();
                string xml="<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>"+
                    "<soap:Body><loginCmsResponse><loginCmsReturn>"+
                    SecurityElement.Escape(acceso)+"</loginCmsReturn></loginCmsResponse></soap:Body></soap:Envelope>";
                return Exito(xml);
            }
            if(solicitud.RequestUri==ArcaHomologacion.WsfeUrl &&
               cuerpo.Contains("FECompConsultar",StringComparison.Ordinal))
            {
                ConsultasWsfe++;
                Assert.Contains("<CbteNro>128</CbteNro>",cuerpo);
                Assert.DoesNotContain("FECAESolicitar",cuerpo);
                return Exito("<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>"+
                    "<soap:Body><FECompConsultarResponse><FECompConsultarResult><ResultGet>"+
                    "<PtoVta>3</PtoVta><CbteTipo>6</CbteTipo><CbteNro>128</CbteNro>"+
                    "<Concepto>2</Concepto><DocTipo>96</DocTipo><DocNro>12345678</DocNro>"+
                    "<CondicionIVAReceptorId>5</CondicionIVAReceptorId><CbteFch>20261025</CbteFch>"+
                    "<ImpTotal>121.00</ImpTotal><ImpTotConc>0.00</ImpTotConc>"+
                    "<ImpNeto>100.00</ImpNeto><ImpOpEx>0.00</ImpOpEx><ImpTrib>0.00</ImpTrib><ImpIVA>21.00</ImpIVA>"+
                    "<MonId>PES</MonId><MonCotiz>1.00</MonCotiz>"+
                    "<FchServDesde>20261025</FchServDesde><FchServHasta>20261025</FchServHasta>"+
                    "<FchVtoPago>20261025</FchVtoPago>"+
                    "<CodAutorizacion>71234567890123</CodAutorizacion><FchVto>20261104</FchVto>"+
                    "</ResultGet></FECompConsultarResult></FECompConsultarResponse></soap:Body></soap:Envelope>");
            }
            if(solicitud.RequestUri==ArcaHomologacion.WsfeUrl)
            {
                ConsultasWsfe++;
                Assert.Contains("FECompUltimoAutorizado",cuerpo);
                Assert.Contains("TOKEN_SIMULADO",cuerpo);
                Assert.Contains("SIGN_SIMULADO",cuerpo);
                Assert.Contains("<Cuit>20123456786</Cuit>",cuerpo);
                Assert.Contains("<PtoVta>3</PtoVta>",cuerpo);
                Assert.Contains("<CbteTipo>6</CbteTipo>",cuerpo);
                string dato=DevolverError
                    ?"<Errors><Err><Code>10001</Code><Msg>Simulado</Msg></Err></Errors>"
                    :"<CbteNro>127</CbteNro>";
                return Exito("<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>"+
                    "<soap:Body><FECompUltimoAutorizadoResponse>"+
                    "<FECompUltimoAutorizadoResult>"+dato+
                    "</FECompUltimoAutorizadoResult></FECompUltimoAutorizadoResponse></soap:Body></soap:Envelope>");
            }
            throw new InvalidOperationException("Se intentó utilizar un endpoint no previsto.");
        }

        private static HttpResponseMessage Exito(string cuerpo)=>new(HttpStatusCode.OK)
        {
            Content=new StringContent(cuerpo,Encoding.UTF8,"text/xml")
        };
    }

    private static (string Archivo,string Carpeta) CertificadoTemporal()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarArca",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var rsa=RSA.Create(2048);
        var solicitud=new CertificateRequest("CN=Pruebas Aparcar",rsa,
            HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var ruta=Path.Combine(dir,"prueba.pfx");
        File.WriteAllBytes(ruta,cert.Export(X509ContentType.Pfx,"clave123"));
        return(ruta,dir);
    }

    [Fact]
    public void SolicitudWsaaTieneServicioWsfeYVentanaTemporal()
    {
        var ahora=new DateTimeOffset(2026,10,25,10,0,0,TimeSpan.Zero);
        string xml=ArcaHomologacion.CrearTicketRequerimiento(ahora,1234);
        Assert.Contains("<service>wsfe</service>",xml);
        Assert.Contains("2026-10-25T09:50:00Z",xml);
        Assert.Contains("2026-10-25T10:10:00Z",xml);
        Assert.Throws<ArgumentOutOfRangeException>(()=>
            ArcaHomologacion.CrearTicketRequerimiento(ahora,0));
    }

    [Fact]
    public async Task ConsultaDeHomologacion_FirmaCmsConsultaWsaaYWsfe_SinEmitir()
    {
        var (ruta,dir)=CertificadoTemporal();
        try
        {
            var falso=new RespuestasFicticias();
            using var http=new HttpClient(falso);
            using var cliente=new ArcaHomologacion(http);
            var resultado=await cliente.ConsultarUltimoAutorizadoAsync(
                ruta,"clave123","20123456786",3,6);
            Assert.Equal(127,resultado.Numero);
            Assert.True(resultado.ExpiraSesionUtc>DateTimeOffset.UtcNow);
            Assert.Equal(1,falso.ConsultasWsaa);
            Assert.Equal(1,falso.ConsultasWsfe);
        }
        finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public async Task RechazoDeWsfe_NoSeConfundeConAutorizacionDeComprobante()
    {
        var (ruta,dir)=CertificadoTemporal();
        try
        {
            var falso=new RespuestasFicticias{DevolverError=true};
            using var http=new HttpClient(falso);
            using var cliente=new ArcaHomologacion(http);
            var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>
                cliente.ConsultarUltimoAutorizadoAsync(ruta,"clave123","20123456786",3,6));
            Assert.Contains("10001",error.Message);
        }
        finally{Directory.Delete(dir,true);}
    }

    [Fact]
    public async Task CuitInvalido_NoEnvíaCertificadoNiSolicitud()
    {
        var (ruta,dir)=CertificadoTemporal();
        try
        {
            var falso=new RespuestasFicticias();
            using var http=new HttpClient(falso);
            using var cliente=new ArcaHomologacion(http);
            await Assert.ThrowsAsync<ArgumentException>(()=>
                cliente.ConsultarUltimoAutorizadoAsync(ruta,"clave123","20345",3,6));
            Assert.Equal(0,falso.ConsultasWsaa);
            Assert.Equal(0,falso.ConsultasWsfe);
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact]
    public async Task ConsultaDeComprobante_UsaFeCompConsultarSinSolicitarNuevoCae()
    {
        var (ruta,dir)=CertificadoTemporal();
        try
        {
            var falso=new RespuestasFicticias();
            using var http=new HttpClient(falso);
            using var cliente=new ArcaHomologacion(http);
            var esperado=new ComprobanteFiscalWsfe(3,6,128,new DateOnly(2026,10,25),
                2,96,12345678,5,121m,0m,100m,0m,0m,21m,
                new[]{new IvaFiscalWsfe(5,100m,21m)},
                new DateOnly(2026,10,25),new DateOnly(2026,10,25),new DateOnly(2026,10,25));
            var consulta=await cliente.ConsultarComprobanteAsync(
                ruta,"clave123","20123456786",esperado);
            Assert.True(consulta.Encontrado);
            Assert.True(consulta.Coincide);
            Assert.True(consulta.TieneCae);
            Assert.Equal("71234567890123",consulta.Cae);
            Assert.Equal(1,falso.ConsultasWsaa);
            Assert.Equal(1,falso.ConsultasWsfe);
        }
        finally{Directory.Delete(dir,true);}
    }

}
