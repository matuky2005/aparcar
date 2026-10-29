using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aparcar.Infrastructure;
using Xunit;

namespace Aparcar.Tests;

public sealed class ArcaEmisionV12Tests
{
    private sealed class SoapFalso : HttpMessageHandler
    {
        public int Solicitados;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            var body=await request.Content!.ReadAsStringAsync(ct);
            string contenido;
            if(request.RequestUri==ArcaHomologacion.WsaaUrl)
            {
                Assert.Contains("loginCms",body);
                var ticket="<loginTicketResponse><header><expirationTime>"+
                    DateTimeOffset.UtcNow.AddMinutes(40).ToString("O")+
                    "</expirationTime></header><credentials><token>TEST_TOKEN</token>"+
                    "<sign>TEST_SIGN</sign></credentials></loginTicketResponse>";
                contenido="<loginCmsResponse><loginCmsReturn>"+
                    System.Security.SecurityElement.Escape(ticket)+
                    "</loginCmsReturn></loginCmsResponse>";
            }
            else if(request.RequestUri==ArcaHomologacion.WsfeUrl)
            {
                Solicitados++;
                Assert.Contains("FECAESolicitar",body);
                Assert.Contains("TEST_TOKEN",body);
                Assert.Contains("TEST_SIGN",body);
                Assert.Contains("<PtoVta>3</PtoVta>",body);
                Assert.Contains("<CbteTipo>6</CbteTipo>",body);
                Assert.Contains("<CbteDesde>1</CbteDesde>",body);
                Assert.Contains("<ImpTotal>121.00</ImpTotal>",body);
                contenido="<FECAESolicitarResponse>"+
                    "<FeCabResp><PtoVta>3</PtoVta><CbteTipo>6</CbteTipo></FeCabResp>"+
                    "<FECAEDetResponse><CbteDesde>1</CbteDesde>"+
                    "<CbteHasta>1</CbteHasta><Resultado>A</Resultado>"+
                    "<CAE>71234567890123</CAE><CAEFchVto>20261105</CAEFchVto>"+
                    "</FECAEDetResponse></FECAESolicitarResponse>";
            }
            else throw new InvalidOperationException("Endpoint no autorizado");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(
                    "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'>"+
                    "<soap:Body>"+contenido+"</soap:Body></soap:Envelope>",
                    Encoding.UTF8,"text/xml")
            };
        }
    }

    [Fact]
    public async Task FeCaeSolicitarSoloHomologacionConIdentidadYCaeVerificados()
    {
        string dir=Path.Combine(Path.GetTempPath(),"FiscalSoap",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa=RSA.Create(2048);
            var req=new CertificateRequest("CN=Emision de prueba",rsa,
                HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using var certificado=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            var ruta=Path.Combine(dir,"test.pfx");
            File.WriteAllBytes(ruta,certificado.Export(X509ContentType.Pfx,"secretoPrueba"));
            var hoy=DateOnly.FromDateTime(DateTime.Today);
            var factura=new ComprobanteFiscalWsfe(3,6,1,hoy,2,96,12345678,5,
                121m,0m,100m,0m,0m,21m,new[]{new IvaFiscalWsfe(5,100m,21m)},
                hoy,hoy,hoy);
            var falso=new SoapFalso();
            using var http=new HttpClient(falso);
            using var arca=new ArcaHomologacion(http);
            var respuesta=await arca.SolicitarCaePruebaAsync(
                ruta,"secretoPrueba","20123456786",factura);
            Assert.Equal(EstadoAutorizacionWsfe.Autorizado,respuesta.Estado);
            Assert.Equal("71234567890123",respuesta.Cae);
            Assert.Equal(1,falso.Solicitados);
        }
        finally{Directory.Delete(dir,true);}
    }
}
