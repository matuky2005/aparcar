using System;
using System.Globalization;
using System.Linq;

namespace Aparcar.Infrastructure;

/// <summary>
/// Identidad comercial para preparar un futuro comprobante completo.
/// Solo se utiliza como snapshot en documentos de HOMOLOGACIÓN; no da validez fiscal.
/// </summary>
public sealed record EmisorDocumentoFiscal(
    string RazonSocial,string NombreFantasia,string Cuit,string DomicilioComercial,
    string CondicionIva,string IngresosBrutos,DateOnly InicioActividades);

public sealed record ReceptorDocumentoFiscal(
    string Nombre,string Domicilio,int DocumentoTipo,long DocumentoNumero,
    int CondicionIvaId,string? Correo=null);

public sealed record DatosDocumentoFiscalPrueba(
    EmisorDocumentoFiscal Emisor,ReceptorDocumentoFiscal Receptor,
    string DescripcionServicio,string CondicionVenta,int? VersionPerfilEmisor=null);

public static class ValidadorDocumentoFiscalPrueba
{
    private static void Texto(string? valor,string campo,int limite,bool requerido=true)
    {
        if(valor is null)
        {
            if(requerido)throw new ArgumentException($"Campo {campo} es obligatorio.");
            return;
        }
        if((requerido && string.IsNullOrWhiteSpace(valor)) ||
           valor.Length>limite || valor.Any(char.IsControl))
            throw new ArgumentException($"Campo {campo} inválido (máximo {limite} caracteres).");
    }

    /// <summary>Validación del perfil reutilizable, independiente de la solicitud concreta.</summary>
    public static void ValidarEmisor(EmisorDocumentoFiscal emisor)
    {
        ArgumentNullException.ThrowIfNull(emisor);
        Texto(emisor.RazonSocial,"razón social emisor",180);
        Texto(emisor.NombreFantasia,"nombre de fantasía",120,false);
        Texto(emisor.DomicilioComercial,"domicilio comercial emisor",250);
        Texto(emisor.CondicionIva,"condición IVA emisor",100);
        Texto(emisor.IngresosBrutos,"Ingresos Brutos",100);
        if(!AdaptadorWsfeFacturador.CuitValido(emisor.Cuit))
            throw new ArgumentException("El CUIT emisor no supera el dígito verificador.");
        if(emisor.InicioActividades<new DateOnly(1900,1,1) ||
           emisor.InicioActividades>DateOnly.FromDateTime(DateTime.Today))
            throw new ArgumentException("Fecha de inicio de actividades inválida.");
    }

    public static void Validar(DatosDocumentoFiscalPrueba datos,SolicitudFiscalPrueba solicitud)
    {
        ArgumentNullException.ThrowIfNull(datos);
        ArgumentNullException.ThrowIfNull(solicitud);
        if(datos.Emisor is null || datos.Receptor is null)
            throw new ArgumentException("Se requieren emisor y receptor.");
        var emisor=datos.Emisor;
        var receptor=datos.Receptor;
        var comprobante=solicitud.Comprobante;

        if(solicitud.Estado!="Preparada")
            throw new InvalidOperationException(
                "La identidad fiscal solo puede registrarse en una solicitud Preparada, antes del envío.");

        ValidarEmisor(emisor);
        if(!string.Equals(emisor.Cuit,solicitud.CuitEmisor,StringComparison.Ordinal))
            throw new ArgumentException("El CUIT emisor debe coincidir con la solicitud reservada.");
        if(emisor.InicioActividades>comprobante.Fecha)
            throw new ArgumentException("El inicio de actividades es posterior a la factura.");
        if(datos.VersionPerfilEmisor is int version && version<1)
            throw new ArgumentException("La versión del perfil debe ser positiva.");

        Texto(receptor.Nombre,"nombre/razón social receptor",180);
        Texto(receptor.Domicilio,"domicilio receptor",250);
        Texto(receptor.Correo,"correo receptor",200,false);
        if(receptor.Correo?.Contains('@')==false && !string.IsNullOrWhiteSpace(receptor.Correo))
            throw new ArgumentException("Correo del receptor inválido.");
        if(receptor.DocumentoTipo!=comprobante.DocTipo ||
           receptor.DocumentoNumero!=comprobante.DocNumero ||
           receptor.CondicionIvaId!=comprobante.CondicionIvaReceptorId)
            throw new ArgumentException(
                "El documento y la condición IVA del receptor deben coincidir con los datos WSFE reservados.");
        Texto(datos.DescripcionServicio,"descripción del servicio",400);
        Texto(datos.CondicionVenta,"condición de venta",120);
        if(comprobante.Concepto is not (2 or 3))
            throw new InvalidOperationException("Este módulo solo admite conceptos de servicios.");
    }
}
