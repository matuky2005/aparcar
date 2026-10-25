using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Aparcar.Infrastructure;

/// <summary>
/// Vista de SOLO LECTURA de una autorización de HOMOLOGACIÓN. No representa
/// una factura comercial y nunca se construye a partir de borradores o estados inciertos.
/// </summary>
public sealed record VistaComprobanteHomologacion(IReadOnlyList<string> Lineas);

public static class PresentacionFiscalHomologacion
{
    private static readonly CultureInfo Argentina=CultureInfo.GetCultureInfo("es-AR");

    public static VistaComprobanteHomologacion Crear(SolicitudFiscalPrueba solicitud)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if(solicitud.Estado!="Autorizada" ||
           solicitud.Cae is null || solicitud.Cae.Length!=14 ||
           !solicitud.Cae.All(x=>x>='0' && x<='9') ||
           solicitud.Vencimiento is null)
            throw new InvalidOperationException(
                "Solo se pueden presentar pruebas autorizadas y conciliadas con CAE de homologación.");

        var c=solicitud.Comprobante;
        AdaptadorWsfeFacturador.Validar(c);
        if(!AdaptadorWsfeFacturador.CuitValido(solicitud.CuitEmisor))
            throw new InvalidOperationException("CUIT emisor inválido.");
        var clase=c.Tipo switch
        {
            1=>"A",
            6=>"B",
            11=>"C",
            _=>throw new InvalidOperationException("Tipo fiscal no admitido.")
        };
        static string Importe(decimal valor)=>valor.ToString("N2",Argentina);

        var lineas=new List<string>
        {
            "APARCAR - AMBIENTE DE HOMOLOGACION",
            "PRUEBA SIN VALIDEZ FISCAL",
            "NO ES FACTURA NI DOCUMENTO TRIBUTARIO",
            $"FACTURA {clase} DE PRUEBA",
            $"Punto de venta: {c.PuntoVenta:D5}",
            $"Numero: {c.Numero:D8}",
            $"Fecha: {c.Fecha:dd/MM/yyyy}",
            $"CUIT emisor: {solicitud.CuitEmisor}",
            $"Origen del cobro: {solicitud.Origen}",
            $"Documento receptor: tipo {c.DocTipo} - {c.DocNumero}",
            $"Condicion IVA receptor (ID ARCA): {c.CondicionIvaReceptorId}",
            $"Concepto: {(c.Concepto==2?"Servicios":c.Concepto==3?"Productos y servicios":"Productos")}"
        };
        if(c.Concepto is 2 or 3)
        {
            lineas.Add($"Servicio: {c.ServicioDesde:dd/MM/yyyy} a {c.ServicioHasta:dd/MM/yyyy}");
            lineas.Add($"Vencimiento de pago: {c.VencimientoPago:dd/MM/yyyy}");
        }
        lineas.Add($"Neto gravado: $ {Importe(c.Neto)}");
        lineas.Add($"No gravado: $ {Importe(c.NoGravado)}");
        lineas.Add($"Exento: $ {Importe(c.Exento)}");
        foreach(var a in c.Alicuotas)
            lineas.Add($"IVA ID {a.Id}: base $ {Importe(a.BaseImponible)} / IVA $ {Importe(a.Importe)}");
        lineas.Add($"IVA total: $ {Importe(c.Iva)}");
        lineas.Add($"Tributos: $ {Importe(c.Tributos)}");
        lineas.Add($"TOTAL: $ {Importe(c.Total)}");
        lineas.Add($"CAE EXCLUSIVO DE PRUEBA: {solicitud.Cae}");
        lineas.Add($"Vencimiento CAE de prueba: {solicitud.Vencimiento:dd/MM/yyyy}");
        lineas.Add("HOMOLOGACION ARCA - NO VERIFICABLE COMO FACTURA REAL");
        lineas.Add("Sin codigo QR de validez fiscal. No entregar al cliente.");
        return new VistaComprobanteHomologacion(lineas.AsReadOnly());
    }
}
