using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Aparcar.Infrastructure;

/// <summary>
/// Resultado de lectura del padrón, no una habilitación automática para facturar.
/// El WS de constancia NO asigna por sí mismo CondicionIVAReceptorId de WSFEv1.
/// </summary>
public sealed record ConstanciaInscripcionArca(
    string Cuit,string RazonSocial,string DomicilioFiscal,string TipoPersona,
    string EstadoClave,IReadOnlyList<string> ImpuestosInformados,
    bool TieneMonotributo,DateTimeOffset ConsultadaUtc);

public sealed partial class ArcaHomologacion
{
    public static readonly Uri ConstanciaHomologacionUrl=
        new("https://awshomo.arca.gob.ar/sr-padron/webservices/personaServiceA5");

    private static readonly XNamespace PadronNs="http://a5.soap.ws.server.puc.sr/";

    private static string? CampoPadron(XElement? node,string name)=>
        node?.Elements().FirstOrDefault(x=>x.Name.LocalName==name)?.Value.Trim();

    /// <summary>
    /// Devuelve la constancia vía WS_SR_constancia_inscripcion v4.1 (getPersona_v2).
    /// La autenticación WSAA solicita un token propio para este servicio, no wsfe.
    /// Endpoints EXCLUSIVAMENTE de homologación, sin almacenar token, sign ni PFX.
    /// </summary>
    public async Task<ConstanciaInscripcionArca> ConsultarConstanciaInscripcionAsync(
        string rutaPfx,string clavePfx,string cuitRepresentada,string cuitConsultada,
        CancellationToken cancellationToken=default)
    {
        if(!AdaptadorWsfeFacturador.CuitValido(cuitRepresentada) ||
           !AdaptadorWsfeFacturador.CuitValido(cuitConsultada))
            throw new ArgumentException("La CUIT representada y la consultada deben ser válidas.");

        var credenciales=await AutenticarWsaaAsync(rutaPfx,clavePfx,cancellationToken,
            "ws_sr_constancia_inscripcion");
        var body=Soap(new XElement(PadronNs+"getPersona_v2",
            new XElement("token",credenciales.Token),
            new XElement("sign",credenciales.Sign),
            new XElement("cuitRepresentada",cuitRepresentada),
            new XElement("idPersona",cuitConsultada)));
        var xml=await EnviarSoap(ConstanciaHomologacionUrl,"",body,cancellationToken);
        return InterpretarConstancia(
            xml.ToString(SaveOptions.DisableFormatting),cuitConsultada,DateTimeOffset.UtcNow);
    }

    public static ConstanciaInscripcionArca InterpretarConstancia(
        string respuestaXml,string cuitEsperada,DateTimeOffset fechaConsultaUtc)
    {
        if(!AdaptadorWsfeFacturador.CuitValido(cuitEsperada))
            throw new ArgumentException("CUIT consultada inválida.");
        var doc=XDocument.Parse(respuestaXml,LoadOptions.None);
        if(doc.Descendants().Any(x=>x.Name.LocalName=="Fault"))
            throw new InvalidOperationException("ARCA rechazó la consulta al padrón.");
        var returnNode=doc.Descendants()
            .FirstOrDefault(x=>x.Name.LocalName=="personaReturn")
            ??throw new InvalidOperationException("ARCA no devolvió una constancia de inscripción.");
        var error=returnNode.Elements().FirstOrDefault(x=>x.Name.LocalName=="errorConstancia");
        if(error is not null)
            throw new InvalidOperationException("ARCA informó que la constancia no es consultable; revisar CUIT y estado registral.");

        var general=returnNode.Elements().FirstOrDefault(x=>x.Name.LocalName=="datosGenerales")
            ??throw new InvalidOperationException("La constancia no incluye datos generales.");
        var cuit=Campo(general,"idPersona");
        if(cuit!=cuitEsperada)
            throw new InvalidOperationException("La CUIT de respuesta no coincide con la solicitada.");
        var estado=Campo(general,"estadoClave")??"";
        if(estado!="ACTIVO")
            throw new InvalidOperationException(
                "El estado de la CUIT no es ACTIVO. No autocompletar datos fiscales.");
        var razonSocial=Campo(general,"razonSocial");
        if(string.IsNullOrWhiteSpace(razonSocial))
        {
            var apellido=Campo(general,"apellido");
            var nombre=Campo(general,"nombre");
            razonSocial=string.Join(" ",new[]{apellido,nombre}.Where(x=>!string.IsNullOrWhiteSpace(x)));
        }
        if(string.IsNullOrWhiteSpace(razonSocial))
            throw new InvalidOperationException("ARCA no informó razón social ni nombre válido.");
        var domicilio=general.Elements().FirstOrDefault(x=>x.Name.LocalName=="domicilioFiscal");
        string domicilioTexto=string.Join(", ",new[]{
            Campo(domicilio,"direccion"),
            Campo(domicilio,"localidad"),
            Campo(domicilio,"descripcionProvincia"),
            Campo(domicilio,"codPostal")
        }.Where(x=>!string.IsNullOrWhiteSpace(x)));
        var impuestos=returnNode.Elements()
            .Where(x=>x.Name.LocalName=="datosRegimenGeneral")
            .SelectMany(x=>x.Elements().Where(y=>y.Name.LocalName=="impuesto"))
            .Select(x=>Campo(x,"descripcionImpuesto"))
            .Where(x=>!string.IsNullOrWhiteSpace(x))
            .Select(x=>x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var monotributo=returnNode.Elements().Any(x=>
            x.Name.LocalName=="datosMonotributo" && x.HasElements);
        return new ConstanciaInscripcionArca(cuit,razonSocial.Trim(),
            domicilioTexto,Campo(general,"tipoPersona")??"",
            estado,Array.AsReadOnly(impuestos),monotributo,fechaConsultaUtc.ToUniversalTime());
    }

    private static string? Campo(XElement? node,string name)=>CampoPadron(node,name);
}
