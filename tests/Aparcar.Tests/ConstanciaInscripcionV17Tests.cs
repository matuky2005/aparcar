using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class ConstanciaInscripcionV17Tests
{
    private const string Datos="<personaReturn><datosGenerales>"+
        "<idPersona>20123456786</idPersona><estadoClave>ACTIVO</estadoClave>"+
        "<tipoPersona>JURIDICA</tipoPersona><razonSocial>ESTACIONAMIENTO EJEMPLO SA</razonSocial>"+
        "<domicilioFiscal><direccion>CALLE 123</direccion><localidad>TAFI VIEJO</localidad>"+
        "<descripcionProvincia>TUCUMAN</descripcionProvincia><codPostal>4103</codPostal>"+
        "</domicilioFiscal></datosGenerales>"+
        "<datosRegimenGeneral><impuesto><descripcionImpuesto>IVA</descripcionImpuesto>"+
        "</impuesto></datosRegimenGeneral></personaReturn>";

    [Fact]
    public void ExtraeDatosDeConstanciaSinInventarIvaDeWsfe()
    {
        var result=ArcaHomologacion.InterpretarConstancia(
            "<Envelope><Body><getPersona_v2Response>"+Datos+
            "</getPersona_v2Response></Body></Envelope>",
            "20123456786",DateTimeOffset.UtcNow);
        Assert.Equal("ESTACIONAMIENTO EJEMPLO SA",result.RazonSocial);
        Assert.Equal("20123456786",result.Cuit);
        Assert.Contains("CALLE 123",result.DomicilioFiscal);
        Assert.Contains("IVA",result.ImpuestosInformados);
        Assert.Equal("ACTIVO",result.EstadoClave);
    }

    [Fact]
    public void NoAdmitePersonaEquivocadaNiInactivaNiErroresDeConstancia()
    {
        Assert.Throws<InvalidOperationException>(()=>ArcaHomologacion.InterpretarConstancia(
            "<r>"+Datos+"</r>","30000000007",DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(()=>ArcaHomologacion.InterpretarConstancia(
            "<r>"+Datos.Replace("ACTIVO","INACTIVO")+"</r>",
            "20123456786",DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(()=>ArcaHomologacion.InterpretarConstancia(
            "<r><personaReturn><errorConstancia><error>Sin datos</error></errorConstancia>"+
            Datos.Replace("<personaReturn>","").Replace("</personaReturn>","")+
            "</personaReturn></r>","20123456786",DateTimeOffset.UtcNow));
    }

    private sealed class ClienteSimulado : HttpMessageHandler
    {
        public int Consultas;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken token)
        {
            var body=await request.Content!.ReadAsStringAsync(token);
            string respuesta;
            if(request.RequestUri==ArcaHomologacion.WsaaUrl)
            {
                var xml=XDocument.Parse(body);
                var cmsBase64=xml.Descendants()
                    .First(x=>x.Name.LocalName=="in0").Value;
                var cms=new SignedCms();
                cms.Decode(Convert.FromBase64String(cmsBase64));
                var tra=Encoding.UTF8.GetString(cms.ContentInfo.Content);
                Assert.Contains("<service>ws_sr_constancia_inscripcion</service>",tra);
                var ticket="<loginTicketResponse><header><expirationTime>"+
                    DateTimeOffset.UtcNow.AddMinutes(30).ToString("O")+
                    "</expirationTime></header><credentials><token>TEST_TOKEN</token>"+
                    "<sign>TEST_SIGN</sign></credentials></loginTicketResponse>";
                respuesta="<loginCmsResponse><loginCmsReturn>"+
                    System.Security.SecurityElement.Escape(ticket)+
                    "</loginCmsReturn></loginCmsResponse>";
            }
            else
            {
                Assert.Equal(ArcaHomologacion.ConstanciaHomologacionUrl,request.RequestUri);
                Assert.Contains("getPersona_v2",body);
                var envelope=XDocument.Parse(body);
                Assert.Equal("30000000007",envelope.Descendants()
                    .Single(x=>x.Name.LocalName=="cuitRepresentada").Value);
                Assert.Equal("20123456786",envelope.Descendants()
                    .Single(x=>x.Name.LocalName=="idPersona").Value);
                Assert.Contains("TEST_TOKEN",body);
                Consultas++;
                respuesta="<getPersona_v2Response>"+Datos+"</getPersona_v2Response>";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(
                    "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>"+
                    "<soap:Body>"+respuesta+"</soap:Body></soap:Envelope>",Encoding.UTF8,"text/xml")
            };
        }
    }

    [Fact]
    public async Task WsaaPadronTieneServicioPropioYConsultaSoloHomologacion()
    {
        var dir=Path.Combine(Path.GetTempPath(),"ArcaPadronV17",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa=RSA.Create(2048);
            var req=new CertificateRequest("CN=Padron prueba",rsa,
                HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var certificado=req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
            var pfx=Path.Combine(dir,"prueba.pfx");
            File.WriteAllBytes(pfx,certificado.Export(X509ContentType.Pfx,"prueba"));
            var fake=new ClienteSimulado();
            using var http=new HttpClient(fake);
            using var arca=new ArcaHomologacion(http);
            var dato=await arca.ConsultarConstanciaInscripcionAsync(
                pfx,"prueba","30000000007","20123456786");
            Assert.Equal("ESTACIONAMIENTO EJEMPLO SA",dato.RazonSocial);
            Assert.Equal(1,fake.Consultas);
        }
        finally {Directory.Delete(dir,true);}
    }
}
