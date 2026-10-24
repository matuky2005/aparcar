using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Aparcar.Domain;

namespace Aparcar.Desktop;

/// <summary>
/// PDF 1.4 sencillo, A4 apaisado, sin dependencias ni plantillas externas.
/// Es un informe NO FISCAL, no una factura electrónica.
/// </summary>
internal static class ExportadorPdf
{
    private static readonly Encoding PdfEncoding=Encoding.Latin1;
    private static readonly CultureInfo Argentina=CultureInfo.GetCultureInfo("es-AR");

    public static string Guardar(ResumenRecaudacion informe,DateTime desde,DateTime hasta)
    {
        ArgumentNullException.ThrowIfNull(informe);
        string carpeta=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Aparcar","Reportes");
        Directory.CreateDirectory(carpeta);
        var destino=Path.Combine(carpeta,
            $"Aparcar_Recaudacion_{desde:yyyyMMdd}_{hasta:yyyyMMdd}_{Guid.NewGuid():N}.pdf");

        var lineas=informe.Lineas.ToList();
        const int porPagina=20;
        int paginas=Math.Max(1,(lineas.Count+porPagina-1)/porPagina);
        var objetos=new List<string?>();
        // Referencias fijas: catalogo, pages, fuente normal, fuente negrita
        objetos.Add(null);
        objetos.Add(null);
        objetos.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objetos.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var paginaIds=new List<int>();

        for(int numero=0;numero<paginas;numero++)
        {
            var contenido=CrearContenido(informe,lineas.Skip(numero*porPagina).Take(porPagina).ToList(),
                desde,hasta,numero+1,paginas);
            int paginaId=objetos.Count+1;
            int contenidoId=paginaId+1;
            paginaIds.Add(paginaId);
            objetos.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contenidoId} 0 R >>");
            int bytes=PdfEncoding.GetByteCount(contenido);
            objetos.Add($"<< /Length {bytes} >>\nstream\n{contenido}\nendstream");
        }
        objetos[0]="<< /Type /Catalog /Pages 2 0 R >>";
        objetos[1]=$"<< /Type /Pages /Kids [{string.Join(" ",paginaIds.Select(i=>$"{i} 0 R"))}] /Count {paginas} >>";

        using var flujo=new MemoryStream();
        void Escribir(string texto)
        {
            byte[] bytes=PdfEncoding.GetBytes(texto);
            flujo.Write(bytes,0,bytes.Length);
        }
        Escribir("%PDF-1.4\n%APARCAR\n");
        var offsets=new List<long>{0};
        for(int i=0;i<objetos.Count;i++)
        {
            offsets.Add(flujo.Position);
            Escribir($"{i+1} 0 obj\n{objetos[i]}\nendobj\n");
        }
        var xref=flujo.Position;
        Escribir($"xref\n0 {objetos.Count+1}\n0000000000 65535 f \n");
        foreach(var pos in offsets.Skip(1))
            Escribir($"{pos:D10} 00000 n \n");
        Escribir($"trailer\n<< /Size {objetos.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(destino,flujo.ToArray());
        return destino;
    }

    private static string CrearContenido(ResumenRecaudacion informe,
        IReadOnlyList<LineaRecaudacion> lineas,DateTime desde,DateTime hasta,int pagina,int total)
    {
        var sb=new StringBuilder();
        // Borde, título, subtítulo e identificación del filtro temporal.
        sb.AppendLine("0.4 w 35 35 772 525 re S");
        Texto(sb,46,548,"APARCAR - REPORTE DE RECAUDACION",17,true);
        Texto(sb,46,527,$"Periodo: {desde:dd/MM/yyyy} al {hasta:dd/MM/yyyy}",10);
        Texto(sb,460,527,$"Generado: {DateTime.Now:dd/MM/yyyy HH:mm}",10);
        Texto(sb,46,510,"Documentacion de consulta. No es un comprobante fiscal.",9);
        sb.AppendLine("46 498 m 796 498 l S");
        string[] columnas={"Fecha/hora","Ticket","Patente","Medio","Importe ARS","Operador"};
        int[] x={48,167,244,355,499,626};
        for(int i=0;i<columnas.Length;i++)Texto(sb,x[i],479,columnas[i],10,true);
        sb.AppendLine("46 470 m 796 470 l S");

        int y=451;
        foreach(var linea in lineas)
        {
            var precio=(linea.ImporteCentavos/100m).ToString("N2",Argentina);
            var medio=linea.Devuelto?"Devol. efectivo":linea.MedioPago;
            string[] c={
                linea.FechaUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                linea.Ticket.ToString("D7",CultureInfo.InvariantCulture),
                linea.Patente,medio,precio,linea.Operador};
            int[] max={18,11,17,21,18,25};
            for(int i=0;i<c.Length;i++)
                Texto(sb,x[i],y,Recortar(c[i],max[i]),9);
            y-=18;
        }
        sb.AppendLine("46 82 m 796 82 l S");
        Texto(sb,46,68,$"Bruto: {(informe.CobrosBrutosCentavos/100m):N2}",9,true);
        Texto(sb,196,68,$"Devol.: {(informe.DevolucionesCentavos/100m):N2}",9);
        Texto(sb,332,68,$"Neto: {(informe.CobrosNetosCentavos/100m):N2}",9,true);
        Texto(sb,473,68,$"Efectivo: {(informe.EfectivoCentavos/100m):N2}",9);
        Texto(sb,629,68,$"Externos: {(informe.ElectronicosCentavos/100m):N2}",9);
        Texto(sb,46,46,"Los pagos externos se registran por declaracion del operador.",8);
        Texto(sb,699,46,$"Pagina {pagina}/{total}",9);
        return sb.ToString();
    }

    private static string Recortar(string valor,int longitud) =>
        valor.Length<=longitud?valor:valor.Substring(0,Math.Max(0,longitud-3))+"...";

    private static void Texto(StringBuilder sb,int x,int y,string texto,int tamano,bool negrita=false)
    {
        texto=Sanear(texto);
        sb.Append("BT /").Append(negrita?"F2":"F1").Append(' ')
          .Append(tamano).Append(" Tf 1 0 0 1 ")
          .Append(x).Append(' ').Append(y)
          .Append(" Tm (").Append(texto).AppendLine(") Tj ET");
    }

    private static string Sanear(string texto)
    {
        var s=new StringBuilder();
        foreach(char c in texto)
        {
            if(c=='\\'||c=='('||c==')')s.Append('\\');
            if(c is '\r' or '\n' or '\t')s.Append(' ');
            else s.Append(c is >=(char)32 and <=(char)255 ? c : '?');
        }
        return s.ToString();
    }
}
