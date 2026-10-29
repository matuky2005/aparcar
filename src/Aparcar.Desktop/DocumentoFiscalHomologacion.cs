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
/// Copia NO FISCAL de una autorización en HOMOLOGACIÓN.
/// No genera QR tributario ni afirma una emisión productiva.
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

        var lineas=vista.Lineas.SelectMany(x=>DividirLinea(x,90)).ToArray();
        const int porPagina=30;
        int paginas=Math.Max(1,(lineas.Length+porPagina-1)/porPagina);
        var folder=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Aparcar","Homologacion");
        Directory.CreateDirectory(folder);
        var ruta=Path.Combine(folder,
            $"Aparcar_PRUEBA_Homologacion_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf");

        var objects=new List<string?>
        {
            null,null,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
        };
        var pageIds=new List<int>();
        for(int p=0;p<paginas;p++)
        {
            var sb=new StringBuilder();
            sb.AppendLine("0.8 w 36 36 523 770 re S");
            EscribirTexto(sb,52,787,"APARCAR - COMPROBANTE DE HOMOLOGACION",15,true);
            EscribirTexto(sb,52,761,"PRUEBA SIN VALIDEZ FISCAL - NO ENTREGAR AL CLIENTE",10,true);
            sb.AppendLine("52 746 m 542 746 l S");
            int y=716;
            foreach(var linea in lineas.Skip(p*porPagina).Take(porPagina))
            {
                EscribirTexto(sb,52,y,linea,8,
                    linea.StartsWith("TOTAL:",StringComparison.Ordinal) ||
                    linea.StartsWith("CAE EXCLUSIVO",StringComparison.Ordinal));
                y-=19;
            }
            sb.AppendLine("52 98 m 542 98 l S");
            EscribirTexto(sb,52,78,"DOCUMENTO DE PRUEBA - NO ES UNA FACTURA ELECTRONICA VALIDA",9,true);
            EscribirTexto(sb,500,48,$"{p+1}/{paginas}",9);
            int pageId=objects.Count+1;
            int streamId=pageId+1;
            pageIds.Add(pageId);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {streamId} 0 R >>");
            var stream=sb.ToString();
            objects.Add($"<< /Length {PdfEncoding.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }
        objects[0]="<< /Type /Catalog /Pages 2 0 R >>";
        objects[1]=$"<< /Type /Pages /Kids [{string.Join(" ",pageIds.Select(x=>$"{x} 0 R"))}] /Count {paginas} >>";

        using var memoria=new MemoryStream();
        void Escribir(string texto)
        {
            var bytes=PdfEncoding.GetBytes(texto);
            memoria.Write(bytes,0,bytes.Length);
        }
        Escribir("%PDF-1.4\n%APARCAR-HOMOLOGACION\n");
        var offsets=new List<long> {0};
        for(int i=0;i<objects.Count;i++)
        {
            offsets.Add(memoria.Position);
            Escribir($"{i+1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref=memoria.Position;
        Escribir($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach(long posicion in offsets.Skip(1))
            Escribir($"{posicion:D10} 00000 n \n");
        Escribir($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(ruta,memoria.ToArray());
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
        var lineas=vista.Lineas.ToArray();
        int siguiente=0;
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
            using var fuerte=new Font("Arial",9f,FontStyle.Bold);
            using var normal=new Font("Arial",8f,FontStyle.Regular);
            float y=12;
            g.DrawString("PRUEBA SIN VALIDEZ FISCAL",fuerte,Brushes.Black,
                new RectangleF(12,y,ancho-24,42));
            y+=48;
            while(siguiente<lineas.Length)
            {
                var linea=lineas[siguiente];
                bool destacada=linea.StartsWith("TOTAL:",StringComparison.Ordinal) ||
                    linea.StartsWith("PRUEBA",StringComparison.Ordinal) ||
                    linea.StartsWith("CAE EXCLUSIVO",StringComparison.Ordinal);
                var fuente=destacada?fuerte:normal;
                float altura=Math.Max(26,g.MeasureString(linea,fuente,ancho-24).Height+9);
                if(y+altura+85>e.PageBounds.Height)break;
                g.DrawString(linea,fuente,Brushes.Black,new RectangleF(12,y,ancho-24,altura));
                y+=altura;
                siguiente++;
            }
            g.DrawString("NO ES FACTURA ELECTRONICA VALIDA",fuerte,Brushes.Black,
                new RectangleF(12,y+12,ancho-24,46));
            e.HasMorePages=siguiente<lineas.Length;
        };
        documento.Print();
    }

    private static IEnumerable<string> DividirLinea(string texto,int max)
    {
        if(string.IsNullOrEmpty(texto)){yield return "";yield break;}
        var resto=texto;
        while(resto.Length>max)
        {
            int corte=resto.LastIndexOf(' ',max);
            if(corte<max/2)corte=max;
            yield return resto[..corte];
            resto=resto[corte..].TrimStart();
        }
        if(resto.Length>0)yield return resto;
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
        foreach(char ch in texto)
        {
            if(ch is '\\' or '(' or ')')sb.Append('\\');
            if(ch is '\r' or '\n' or '\t')sb.Append(' ');
            else sb.Append(ch is >=(char)32 and <=(char)255 ? ch : '?');
        }
        return sb.ToString();
    }
}
