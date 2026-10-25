using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Aparcar.Infrastructure;

/// <summary>
/// Especificación ARCA QR v1. PREPARACIÓN AISLADA: ningún flujo actual puede
/// emitir en producción ni generar QR desde SolicitudFiscalPrueba.
/// Un valor Ambiente=Produccion aquí NO reemplaza homologación ni verifica CAE:
/// la integración futura deberá suministrar solo autorizaciones reales
/// consultadas/persistidas por WSFEv1 de producción.
/// </summary>
public enum AmbienteQrArca { Homologacion, Produccion }

public sealed record DatosQrArca(
    AmbienteQrArca Ambiente,string CuitEmisor,DateOnly Fecha,
    int PuntoVenta,int TipoComprobante,long NumeroComprobante,
    decimal Total,int DocumentoTipo,long DocumentoNumero,string Cae);

public static class EspecificacionQrArca
{
    private const string BaseUrl="https://www.arca.gob.ar/fe/qr/";

    public static string CrearUrl(DatosQrArca datos)
    {
        ArgumentNullException.ThrowIfNull(datos);
        if(datos.Ambiente!=AmbienteQrArca.Produccion)
            throw new InvalidOperationException(
                "No se genera QR fiscal para una factura de homologación.");
        if(!AdaptadorWsfeFacturador.CuitValido(datos.CuitEmisor))
            throw new ArgumentException("CUIT emisor inválido.");
        if(datos.PuntoVenta is <1 or >99998 ||
           datos.TipoComprobante is not (1 or 6 or 11) ||
           datos.NumeroComprobante is <1 or >99999999)
            throw new ArgumentException("Identificación fiscal fuera de rango.");
        if(datos.Total<=0m || datos.Total>=10000000000000m ||
           decimal.Round(datos.Total,2)!=datos.Total)
            throw new ArgumentException("Importe total inválido para QR versión 1.");
        if(datos.Cae?.Length!=14 || datos.Cae.Any(x=>x<'0'||x>'9') ||
           datos.Cae[0]=='0' ||
           !long.TryParse(datos.Cae,NumberStyles.None,CultureInfo.InvariantCulture,out long cae))
            throw new ArgumentException("CAE inválido para QR versión 1.");
        if(datos.DocumentoTipo is not (80 or 86 or 96 or 99) ||
           datos.DocumentoNumero<0 ||
           (datos.DocumentoTipo==99 && datos.DocumentoNumero!=0) ||
           (datos.DocumentoTipo!=99 && datos.DocumentoNumero<=0))
            throw new ArgumentException("Documento receptor inválido.");

        // Orden y tipos alineados con la especificación oficial:
        // https://www.arca.gob.ar/fe/qr/documentos/QRespecificaciones.pdf
        var payload=new Dictionary<string,object>
        {
            ["ver"]=1,
            ["fecha"]=datos.Fecha.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            ["cuit"]=long.Parse(datos.CuitEmisor,CultureInfo.InvariantCulture),
            ["ptoVta"]=datos.PuntoVenta,
            ["tipoCmp"]=datos.TipoComprobante,
            ["nroCmp"]=datos.NumeroComprobante,
            ["importe"]=datos.Total,
            ["moneda"]="PES",
            ["ctz"]=1
        };
        // ARCA: para consumidor final sin identificar, omitir ambos campos.
        if(datos.DocumentoTipo!=99)
        {
            payload["tipoDocRec"]=datos.DocumentoTipo;
            payload["nroDocRec"]=datos.DocumentoNumero;
        }
        payload["tipoCodAut"]="E";
        payload["codAut"]=cae;
        var json=JsonSerializer.Serialize(payload);
        return BaseUrl+"?p="+Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
