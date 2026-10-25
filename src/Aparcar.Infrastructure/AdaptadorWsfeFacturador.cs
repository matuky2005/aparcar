using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Aparcar.Infrastructure;

/// <summary>
/// Adaptación independiente de Facturador.ARCA/Wsfe/WsfeCaeRequestBuilder:
/// la validación se hace ANTES de permitir un envío, nunca calcula
/// condiciones tributarias o alícuotas automáticamente.
/// </summary>
public sealed record IvaFiscalWsfe(int Id,decimal BaseImponible,decimal Importe);

public sealed record ComprobanteFiscalWsfe(
    int PuntoVenta,int Tipo,long Numero,DateOnly Fecha,
    int Concepto,int DocTipo,long DocNumero,int CondicionIvaReceptorId,
    decimal Total,decimal NoGravado,decimal Neto,decimal Exento,
    decimal Tributos,decimal Iva,IReadOnlyList<IvaFiscalWsfe> Alicuotas,
    DateOnly? ServicioDesde=null,DateOnly? ServicioHasta=null,
    DateOnly? VencimientoPago=null);

public enum EstadoAutorizacionWsfe { Autorizado, Rechazado, Indeterminado }

public sealed record ResultadoAutorizacionWsfe(
    EstadoAutorizacionWsfe Estado,string? Cae,DateOnly? Vencimiento,
    IReadOnlyList<int> CodigosError,string Detalle);

public sealed record ResultadoConsultaWsfe(
    bool Encontrado,bool Coincide,bool TieneCae,string? Cae,DateOnly? Vencimiento,
    string Detalle);

/// <summary>
/// Construcción y verificación fiscal FUERA del circuito de caja.
/// No tiene red ni métodos para solicitar CAE.
/// </summary>
public static class AdaptadorWsfeFacturador
{
    public const string Namespace="http://ar.gov.afip.dif.FEV1/";
    private static readonly XNamespace Ar=Namespace;
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    public static XElement PrepararFeCaeReq(ComprobanteFiscalWsfe comprobante)
    {
        Validar(comprobante);
        var detalle=new XElement(Ar+"FECAEDetRequest",
            new XElement(Ar+"Concepto",comprobante.Concepto),
            new XElement(Ar+"DocTipo",comprobante.DocTipo),
            new XElement(Ar+"DocNro",comprobante.DocNumero),
            new XElement(Ar+"CbteDesde",comprobante.Numero),
            new XElement(Ar+"CbteHasta",comprobante.Numero),
            new XElement(Ar+"CbteFch",Fecha(comprobante.Fecha)),
            new XElement(Ar+"ImpTotal",Importe(comprobante.Total)),
            new XElement(Ar+"ImpTotConc",Importe(comprobante.NoGravado)),
            new XElement(Ar+"ImpNeto",Importe(comprobante.Neto)),
            new XElement(Ar+"ImpOpEx",Importe(comprobante.Exento)),
            new XElement(Ar+"ImpTrib",Importe(comprobante.Tributos)),
            new XElement(Ar+"ImpIVA",Importe(comprobante.Iva)));
        if(comprobante.Concepto is 2 or 3)
        {
            detalle.Add(
                new XElement(Ar+"FchServDesde",Fecha(comprobante.ServicioDesde!.Value)),
                new XElement(Ar+"FchServHasta",Fecha(comprobante.ServicioHasta!.Value)),
                new XElement(Ar+"FchVtoPago",Fecha(comprobante.VencimientoPago!.Value)));
        }
        detalle.Add(new XElement(Ar+"MonId","PES"),
            new XElement(Ar+"MonCotiz","1"),
            new XElement(Ar+"CondicionIVAReceptorId",comprobante.CondicionIvaReceptorId));
        if(comprobante.Alicuotas.Count>0)
            detalle.Add(new XElement(Ar+"Iva",comprobante.Alicuotas.Select(x=>
                new XElement(Ar+"AlicIva",
                    new XElement(Ar+"Id",x.Id),
                    new XElement(Ar+"BaseImp",Importe(x.BaseImponible)),
                    new XElement(Ar+"Importe",Importe(x.Importe))))));
        return new XElement(Ar+"FeCAEReq",
            new XElement(Ar+"FeCabReq",
                new XElement(Ar+"CantReg",1),
                new XElement(Ar+"PtoVta",comprobante.PuntoVenta),
                new XElement(Ar+"CbteTipo",comprobante.Tipo)),
            new XElement(Ar+"FeDetReq",detalle));
    }

    public static void Validar(ComprobanteFiscalWsfe c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if(c.Tipo is not (1 or 6 or 11))
            throw new InvalidOperationException("Solo se preparan Facturas A, B o C (sin notas de crédito).");
        if(c.PuntoVenta is <1 or >99998 || c.Numero is <1 or >99999999)
            throw new InvalidOperationException("Punto de venta o número fuera del rango permitido.");
        if(c.Concepto is not (1 or 2 or 3))
            throw new InvalidOperationException("Concepto fiscal inválido.");
        if(c.DocTipo is not (80 or 86 or 96 or 99) || c.DocNumero<0)
            throw new InvalidOperationException("Documento del receptor inválido.");
        if(c.Tipo==1 && (c.DocTipo!=80 || c.DocNumero<=0))
            throw new InvalidOperationException("Factura A exige CUIT del receptor.");
        if(c.DocTipo==99 && (c.Tipo!=6 || c.DocNumero!=0))
            throw new InvalidOperationException("Sin identificar solo corresponde a Factura B con documento 0.");
        if(c.DocTipo!=99 && c.DocNumero==0)
            throw new InvalidOperationException("Informá el número del documento del receptor.");
        if(c.CondicionIvaReceptorId<=0)
            throw new InvalidOperationException("La condición IVA del receptor debe validarse contra ARCA.");
        if(c.Alicuotas is null)
            throw new InvalidOperationException("Falta detalle de alícuotas IVA.");
        if(c.Total<=0 || c.NoGravado<0 || c.Neto<0 || c.Exento<0 ||
           c.Tributos<0 || c.Iva<0)
            throw new InvalidOperationException("Importes fiscales negativos o total cero.");
        if(c.Tributos!=0)
            throw new InvalidOperationException("No se implementó detalle de tributos: no se puede autorizar.");
        if(!Cuadrado(c.Total,c.NoGravado+c.Neto+c.Exento+c.Tributos+c.Iva))
            throw new InvalidOperationException("El total no coincide con los conceptos fiscales.");
        if(c.Tipo==11 && (c.Iva!=0 || c.Alicuotas.Count>0))
            throw new InvalidOperationException("Factura C no puede desglosar IVA.");
        if(c.Iva>0 && c.Alicuotas.Count==0)
            throw new InvalidOperationException("Falta el detalle de las alícuotas de IVA.");
        if(c.Alicuotas.Any(x=>x.Id<=0 || x.BaseImponible<0 || x.Importe<0) ||
           c.Alicuotas.GroupBy(x=>x.Id).Any(g=>g.Count()>1))
            throw new InvalidOperationException("Alícuotas IVA inválidas o repetidas.");
        if(c.Alicuotas.Count>0 &&
            (!Cuadrado(c.Neto,c.Alicuotas.Sum(x=>x.BaseImponible)) ||
             !Cuadrado(c.Iva,c.Alicuotas.Sum(x=>x.Importe))))
            throw new InvalidOperationException("Bases e IVA por alícuota no coinciden con los totales.");
        if(c.Concepto is 2 or 3)
        {
            if(c.ServicioDesde is null || c.ServicioHasta is null || c.VencimientoPago is null ||
                c.ServicioDesde>c.ServicioHasta || c.VencimientoPago<c.Fecha)
                throw new InvalidOperationException("Faltan fechas coherentes de servicio y vencimiento de pago.");
        }
        else if(c.ServicioDesde is not null || c.ServicioHasta is not null ||
                c.VencimientoPago is not null)
            throw new InvalidOperationException("Las fechas de servicio no corresponden a Concepto 1.");
    }

    private static string Fecha(DateOnly d)=>d.ToString("yyyyMMdd",Inv);
    private static string Importe(decimal x)=>x.ToString("0.00",Inv);
    private static bool Cuadrado(decimal x,decimal y)=>Math.Abs(x-y)<=0.01m;

    // Adaptado de Facturador.ARCA/Wsfe/WsfeResponseParser:
    // identidad y CAE deben verificarse ANTES de marcar una factura autorizada.
    public static ResultadoAutorizacionWsfe InterpretarAutorizacion(
        string xml,ComprobanteFiscalWsfe esperado)
    {
        var doc=XmlSeguro(xml);
        var errores=doc.Descendants().Where(x=>x.Name.LocalName=="Err")
            .Select(e=>int.TryParse(Campo(e,"Code"),out var i)?i:-1).ToArray();
        var detalle=doc.Descendants().FirstOrDefault(x=>x.Name.LocalName is "FECAEDetResponse" or "FEDetResponse");
        var cabecera=doc.Descendants().FirstOrDefault(x=>x.Name.LocalName=="FeCabResp");
        if(detalle is null || cabecera is null ||
           Campo(cabecera,"PtoVta")!=esperado.PuntoVenta.ToString(Inv) ||
           Campo(cabecera,"CbteTipo")!=esperado.Tipo.ToString(Inv) ||
           Campo(detalle,"CbteDesde")!=esperado.Numero.ToString(Inv) ||
           Campo(detalle,"CbteHasta")!=esperado.Numero.ToString(Inv))
            return new(EstadoAutorizacionWsfe.Indeterminado,null,null,errores,
                "Respuesta incompleta o identidad fiscal distinta: reconciliar antes de reintentar.");
        var resultado=Campo(detalle,"Resultado");
        if(resultado=="R")
            return new(EstadoAutorizacionWsfe.Rechazado,null,null,errores,
                "ARCA rechazó explícitamente el comprobante.");
        if(resultado!="A")
            return new(EstadoAutorizacionWsfe.Indeterminado,null,null,errores,
                "ARCA no informó una aprobación/rechazo inequívoco.");
        var cae=Campo(detalle,"CAE");
        var vence=Campo(detalle,"CAEFchVto");
        if(cae is null || cae.Length!=14 || !cae.All(char.IsDigit) ||
           !DateOnly.TryParseExact(vence,"yyyyMMdd",Inv,DateTimeStyles.None,out var vencimiento))
            return new(EstadoAutorizacionWsfe.Indeterminado,null,null,errores,
                "Respuesta de aprobación sin CAE/vencimiento válidos; verificar en ARCA.");
        return new(EstadoAutorizacionWsfe.Autorizado,cae,vencimiento,errores,
            "Autorización verificada contra tipo, punto de venta y número esperado.");
    }

    public static ResultadoConsultaWsfe InterpretarConsulta(
        string xml,ComprobanteFiscalWsfe esperado)
    {
        var doc=XmlSeguro(xml);
        var consulta=doc.Descendants().FirstOrDefault(x=>x.Name.LocalName=="ResultGet");
        if(consulta is null)
        {
            var contieneErrores=doc.Descendants().Any(x=>x.Name.LocalName=="Err"||x.Name.LocalName=="Fault");
            return new(false,false,false,null,null,contieneErrores
                ?"ARCA devolvió errores: no asumir comprobante inexistente."
                :"Sin detalle de comprobante: no asumir número disponible.");
        }
        var coincide=Cantidad(consulta,"PtoVta",esperado.PuntoVenta) &&
           Cantidad(consulta,"CbteTipo",esperado.Tipo) &&
           Cantidad(consulta,"CbteNro",esperado.Numero) &&
           Cantidad(consulta,"Concepto",esperado.Concepto) &&
           Cantidad(consulta,"DocTipo",esperado.DocTipo) &&
           Cantidad(consulta,"DocNro",esperado.DocNumero) &&
           Cantidad(consulta,"CondicionIVAReceptorId",esperado.CondicionIvaReceptorId) &&
           Campo(consulta,"CbteFch")==Fecha(esperado.Fecha) &&
           Dinero(consulta,"ImpTotal",esperado.Total) &&
           Dinero(consulta,"ImpTotConc",esperado.NoGravado) &&
           Dinero(consulta,"ImpNeto",esperado.Neto) &&
           Dinero(consulta,"ImpOpEx",esperado.Exento) &&
           Dinero(consulta,"ImpTrib",esperado.Tributos) &&
           Dinero(consulta,"ImpIVA",esperado.Iva) &&
           Campo(consulta,"MonId")=="PES" &&
           Dinero(consulta,"MonCotiz",1m) &&
           (esperado.Concepto==1 ||
               (Campo(consulta,"FchServDesde")==Fecha(esperado.ServicioDesde!.Value) &&
                Campo(consulta,"FchServHasta")==Fecha(esperado.ServicioHasta!.Value) &&
                Campo(consulta,"FchVtoPago")==Fecha(esperado.VencimientoPago!.Value)));
        if(!coincide)
            return new(true,false,false,null,null,
                "Identidad o detalle fiscal distinto: requiere revisión manual, no reemitir.");
        var cae=Campo(consulta,"CodAutorizacion");
        var fecha=Campo(consulta,"FchVto");
        if(cae?.Length!=14 || !cae.All(char.IsDigit) ||
           !DateOnly.TryParseExact(fecha,"yyyyMMdd",Inv,DateTimeStyles.None,out var vto))
            return new(true,true,false,null,null,
                "Datos coincidentes, pero código de autorización no verificable.");
        return new(true,true,true,cae,vto,"Comprobante y autorización conciliados.");
    }

    private static bool Cantidad(XElement elemento,string nombre,long valor)=>
        long.TryParse(Campo(elemento,nombre),NumberStyles.Integer,Inv,out var v)&&v==valor;

    private static bool Dinero(XElement elemento,string nombre,decimal valor)=>
        decimal.TryParse(Campo(elemento,nombre),NumberStyles.Number,Inv,out var v)&&Cuadrado(v,valor);

    private static string? Campo(XElement element,string name)=>
        element.Elements().FirstOrDefault(x=>x.Name.LocalName==name)?.Value;

    private static XDocument XmlSeguro(string xml)
    {
        using var sr=new StringReader(xml);
        using var xr=XmlReader.Create(sr,new XmlReaderSettings{
            XmlResolver=null,DtdProcessing=DtdProcessing.Prohibit,MaxCharactersInDocument=2_000_000
        });
        return XDocument.Load(xr);
    }
}
