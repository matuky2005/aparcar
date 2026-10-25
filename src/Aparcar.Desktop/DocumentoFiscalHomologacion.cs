using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Aparcar.Infrastructure;

namespace Aparcar.Desktop;

/// <summary>
/// Copia local de un resultado de HOMOLOGACIÓN. Nunca produce factura comercial
/// ni QR de verificación fiscal. El modelo ya exige estado autorizado y CAE de prueba.
/// </summary>
internal static class DocumentoFiscalHomologacion
{
    private static readonly Encoding PdfEncoding=Encoding.Latin1;
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

    public static string GuardarPdf(VistaComprobanteHomologacion vista)
    {
        ArgumentNullException.ThrowIfNull(vista);
        if(vista.Lineas.Count==0)
            throw new ArgumentException("No hay contenido de homologación.",nameof(vista));
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Aparcar","Homologacion");
        Directory.CreateDirectory(folder);
        var ruta=Path.Combine(folder,
            $"Aparcar_PRUEBA_Homologacion_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf");

        var contenido=new StringBuilder();
        contenido.AppendLine("0.8 w 36 36 523 770 re S");
        EscribirTexto(contenido,52,787,"APARCAR - COMPROBANTE DE HOMOLOGACION",15,true);
        EscribirTexto(contenido,52,760,"PRUEBA SIN VALIDEZ FISCAL - NO ENTREGAR AL CLIENTE",10,true);
        contenido.AppendLine("52 746 m 542 746 l S");
        int y=718;
        foreach(var linea in vista.Lineas)
        {
            if(y<122)
                throw new InvalidOperationException("El comprobante de prueba excede una página.");
            EscribirTexto(contenido,52,y,linea,9,
                linea.StartsWith("TOTAL:",StringComparison.Ordinal) ||
                linea.StartsWith("CAE EXCLUSIVO",StringComparison.Ordinal));
            y-=23;
        }
        contenido.AppendLine("52 98 m 542 98 l S");
        EscribirTexto(contenido,52,78,"DOCUMENTO DE PRUEBA - NO ES UNA FACTURA ELECTRONICA VALIDA",9,true);

        var streamText=contenido.ToString();
        var objects=new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [5 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents 6 0 R >>",
            $"<< /Length {PdfEncoding.GetByteCount(streamText)} >>\nstream\n{streamText}\nendstream"
        };

        using var flujo=new MemoryStream();
        void Escribir(string texto)
        {
            var bytes=PdfEncoding.GetBytes(texto);
            flujo.Write(bytes,0,bytes.Length);
        }
        Escribir("%PDF-1.4\n%APARCAR-HOMOLOGACION\n");
        var offsets=new List<long> {0};
        for(int i=0;i<objects.Count;i++)
        {
            offsets.Add(flujo.Position);
            Escribir($"{i+1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref=flujo.Position;
        Escribir($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets.Skip(1))
            Escribir($"{offset:D10} 00000 n \n");
        Escribir($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(ruta,flujo.ToArray());
        return ruta;
    }

    public static void Imprimir(VistaComprobanteHomologacion vista,string impresora,int anchoMm)
    {
        ArgumentNullException.ThrowIfNull(vista);
        if(vista.Lineas.Count==0)throw new ArgumentException("No hay contenido de prueba.");
        if(anchoMm is not (58 or 80))
            throw new ArgumentOutOfRangeException(nameof(anchoMm),"Solo papel 58 u 80 mm.");
        if(string.IsNullOrWhiteSpace(impresora))
            throw new InvalidOperationException("Configurá la impresora antes de imprimir.");
        using var documento=new PrintDocument();
        documento.PrinterSettings.PrinterName=impresora;
        if(!documento.PrinterSettings.IsValid)
            throw new InvalidOperationException("La impresora seleccionada no está disponible.");
        int ancho=anchoMm==58?228:315;
        documento.DocumentName="Aparcar - HOMOLOGACION - NO FISCAL";
        documento.PrintController=new StandardPrintController();
        documento.DefaultPageSettings.PaperSize=new PaperSize("Prueba de homologacion",ancho,1250);
        documento.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        documento.PrintPage+=(_,e)=>
        {
            if(e.Graphics is null)throw new InvalidOperationException("No hay superficie de impresión.");
            var g=e.Graphics;
            g.PageUnit=GraphicsUnit.Display;
            g.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            g.Clear(Color.White);
            using var fuerte=new Font("Arial",10f,FontStyle.Bold);
            using var normal=new Font("Arial",8.5f,FontStyle.Regular);
            float y=12;
            foreach(var linea in vista.Lineas)
            {
                var destacada=linea.StartsWith("TOTAL:",StringComparison.Ordinal) ||
                    linea.StartsWith("PRUEBA",StringComparison.Ordinal) ||
                    linea.StartsWith("CAE EXCLUSIVO",StringComparison.Ordinal);
                var fuente=destacada?fuerte:normal;
                var size=g.MeasureString(linea,fuente,ancho-24);
                float altura=Math.Max(24,size.Height+8);
                g.DrawString(linea,fuente,Brushes.Black,new RectangleF(12,y,ancho-24,altura));
                y+=altura;
            }
            g.DrawString("NO ES FACTURA ELECTRONICA VALIDA",fuerte,Brushes.Black,
                new RectangleF(12,y+8,ancho-24,40));
            e.HasMorePages=false;
        };
        documento.Print();
    }

    private static void EscribirTexto(StringBuilder sb,int x,int y,string texto,int tamano,bool bold=false)
    {
        sb.Append("BT /").Append(bold?"F2":"F1").Append(' ')
            .Append(tamano.ToString(Inv)).Append(" Tf 1 0 0 1 ")
            .Append(x).Append(' ').Append(y).Append(" Tm (")
            .Append(EscaparPdf(texto)).AppendLine(") Tj ET");
    }

    private static string EscaparPdf(string texto)
    {
        var sb=new StringBuilder(texto.Length);
        foreach(var ch in texto)
        {
            if(ch is '\\' or '(' or ')')sb.Append('\\');
            if(ch is '\r' or '\n' or '\t')sb.Append(' ');
            else sb.Append(ch is >=(char)32 and <=(char)255 ? ch : '?');
        }
        return sb.ToString();
    }
}
