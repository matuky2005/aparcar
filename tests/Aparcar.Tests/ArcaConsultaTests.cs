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
}
