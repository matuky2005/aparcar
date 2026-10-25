using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Aparcar.Infrastructure;

/// <summary>
/// WSAA + WSFEv1 SOLO LECTURA en HOMOLOGACIÓN.
/// No solicita CAE, no guarda claves/tokens y no contiene endpoint de producción.
/// Para usarlo se requiere certificado PFX de homologación habilitado para wsfe.
/// </summary>
public sealed class ArcaHomologacion : IDisposable
{
    public static readonly Uri WsaaUrl =
        new("https://wsaahomo.afip.gov.ar/ws/services/LoginCms");
    public static readonly Uri WsfeUrl =
        new("https://wswhomo.afip.gov.ar/wsfev1/service.asmx");

    private const string WsaaNs="http://wsaa.view.sua.dvadac.desein.afip.gov";
    private const string WsfeNs="http://ar.gov.afip.dif.FEV1/";
    private static readonly XNamespace SoapNs="http://schemas.xmlsoap.org/soap/envelope/";

    private readonly HttpClient _http;
    private readonly bool _disponeCliente;

    public ArcaHomologacion(HttpClient? cliente=null)
    {
        _disponeCliente=cliente is null;
        _http=cliente ?? new HttpClient {Timeout=TimeSpan.FromSeconds(25)};
    }

    public void Dispose()
    {
        if(_disponeCliente)_http.Dispose();
    }

    public static string CrearTicketRequerimiento(DateTimeOffset fechaUtc,int uniqueId)
    {
        if(uniqueId<=0)throw new ArgumentOutOfRangeException(nameof(uniqueId));
        var utc=fechaUtc.ToUniversalTime();
        return new XDocument(
            new XElement("loginTicketRequest",
                new XAttribute("version","1.0"),
                new XElement("header",
                    new XElement("uniqueId",uniqueId),
                    new XElement("generationTime",utc.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ssZ",CultureInfo.InvariantCulture)),
                    new XElement("expirationTime",utc.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ssZ",CultureInfo.InvariantCulture))),
                new XElement("service","wsfe"))).ToString(SaveOptions.DisableFormatting);
    }

    public static string FirmarTicket(string ticketXml,X509Certificate2 certificado)
    {
        if(!certificado.HasPrivateKey)
            throw new InvalidOperationException("El certificado de ARCA no contiene clave privada.");
        var mensaje=new ContentInfo(Encoding.UTF8.GetBytes(ticketXml));
        var cms=new SignedCms(mensaje,detached:false);
        var firmante=new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber,certificado)
        {
            IncludeOption=X509IncludeOption.EndCertOnly
        };
        cms.ComputeSignature(firmante);
        return Convert.ToBase64String(cms.Encode());
    }

    public static (string Token,string Sign,DateTimeOffset ExpiraUtc) LeerTicketAcceso(string xml)
    {
        var doc=LeerXmlSeguro(xml);
        var nodo=doc.Descendants().FirstOrDefault(e=>e.Name.LocalName=="loginTicketResponse")
            ?? throw new InvalidOperationException("WSAA no devolvió un ticket de acceso válido.");
        var token=nodo.Descendants().FirstOrDefault(e=>e.Name.LocalName=="token")?.Value;
        var sign=nodo.Descendants().FirstOrDefault(e=>e.Name.LocalName=="sign")?.Value;
        var vencimiento=nodo.Descendants().FirstOrDefault(e=>e.Name.LocalName=="expirationTime")?.Value;
        if(string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(sign)||
           !DateTimeOffset.TryParse(vencimiento,CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,out var expira))
            throw new InvalidOperationException("WSAA devolvió credenciales incompletas.");
        return (token,sign,expira.ToUniversalTime());
    }

    private static XDocument LeerXmlSeguro(string texto)
    {
        using var sr=new StringReader(texto);
        using var reader=XmlReader.Create(sr,new XmlReaderSettings{
            DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,
            MaxCharactersInDocument=2_000_000,MaxCharactersFromEntities=0
        });
        return XDocument.Load(reader);
    }

    private static string Soap(XElement accion)
    {
        var doc=new XDocument(new XElement(SoapNs+"Envelope",
            new XAttribute(XNamespace.Xmlns+"soap",SoapNs.NamespaceName),
            new XElement(SoapNs+"Body",accion)));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private async Task<XDocument> EnviarSoap(Uri url,string action,string body,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Post,url);
        req.Content=new StringContent(body,Encoding.UTF8,"text/xml");
        req.Headers.TryAddWithoutValidation("SOAPAction",action.Length==0?"\"\"":$"\"{action}\"");
        using var resp=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARCA no respondió correctamente (HTTP {(int)resp.StatusCode}).");
        if(resp.Content.Headers.ContentLength>2_000_000)
            throw new InvalidOperationException("ARCA devolvió una respuesta demasiado grande.");
        string xml=await resp.Content.ReadAsStringAsync(ct);
        if(xml.Length>2_000_000)throw new InvalidOperationException("La respuesta excede el tamaño permitido.");
        var doc=LeerXmlSeguro(xml);
        var fault=doc.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Fault");
        if(fault is not null)
            throw new InvalidOperationException("ARCA rechazó la solicitud SOAP. Revisá certificado, relaciones y datos enviados.");
        return doc;
    }

    public async Task<(int Numero,DateTimeOffset ExpiraSesionUtc)> ConsultarUltimoAutorizadoAsync(
        string pfxRuta,string clavePfx,string cuit,int puntoVenta,int tipoComprobante,
        CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(pfxRuta)||!File.Exists(pfxRuta))
            throw new FileNotFoundException("Seleccioná un certificado PFX de homologación.");
        if(string.IsNullOrWhiteSpace(cuit)||cuit.Length!=11 || !cuit.All(char.IsDigit))
            throw new ArgumentException("El CUIT debe tener 11 dígitos.");
        if(puntoVenta is <1 or >99999)throw new ArgumentOutOfRangeException(nameof(puntoVenta));
        if(tipoComprobante is <1 or >999)throw new ArgumentOutOfRangeException(nameof(tipoComprobante));

        using var cert=X509CertificateLoader.LoadPkcs12FromFile(
            pfxRuta,clavePfx,X509KeyStorageFlags.EphemeralKeySet);
        if(!cert.HasPrivateKey||DateTime.UtcNow<cert.NotBefore.ToUniversalTime()||
           DateTime.UtcNow>=cert.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("El certificado debe tener clave privada y estar vigente.");

        var ticket=CrearTicketRequerimiento(DateTimeOffset.UtcNow,
            RandomNumberGenerator.GetInt32(1,int.MaxValue));
        var cms=FirmarTicket(ticket,cert);
        XNamespace authNs=WsaaNs;
        var requestWsaa=Soap(new XElement(authNs+"loginCms",
            new XElement(authNs+"in0",cms)));
        var responseWsaa=await EnviarSoap(WsaaUrl,"",requestWsaa,cancellationToken);
        var interno=responseWsaa.Descendants().FirstOrDefault(e=>e.Name.LocalName=="loginCmsReturn")?.Value
            ??throw new InvalidOperationException("WSAA no devolvió una respuesta de autenticación.");
        var acceso=LeerTicketAcceso(interno);
        if(acceso.ExpiraUtc<=DateTimeOffset.UtcNow.AddMinutes(1))
            throw new InvalidOperationException("WSAA devolvió un ticket vencido o demasiado próximo a vencer.");
        XNamespace fe=WsfeNs;
        var requestWsfe=Soap(new XElement(fe+"FECompUltimoAutorizado",
            new XElement(fe+"Auth",
                new XElement(fe+"Token",acceso.Token),
                new XElement(fe+"Sign",acceso.Sign),
                new XElement(fe+"Cuit",cuit)),
            new XElement(fe+"PtoVta",puntoVenta),
            new XElement(fe+"CbteTipo",tipoComprobante)));
        var respuesta=await EnviarSoap(WsfeUrl,WsfeNs+"FECompUltimoAutorizado",
            requestWsfe,cancellationToken);
        var result=respuesta.Descendants().FirstOrDefault(e=>e.Name.LocalName=="FECompUltimoAutorizadoResult")
            ??throw new InvalidOperationException("WSFEv1 no devolvió el resultado esperado.");
        var error=result.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Err");
        if(error is not null)
        {
            var codigo=error.Elements().FirstOrDefault(e=>e.Name.LocalName=="Code")?.Value;
            throw new InvalidOperationException($"WSFEv1 informó un error (código {codigo??"desconocido"}).");
        }
        var ultimo=result.Elements().FirstOrDefault(e=>e.Name.LocalName=="CbteNro")?.Value;
        if(!int.TryParse(ultimo,NumberStyles.None,CultureInfo.InvariantCulture,out var numero) || numero<0)
            throw new InvalidOperationException("WSFEv1 devolvió un número de comprobante inválido.");
        return (numero,acceso.ExpiraUtc);
    }
}
