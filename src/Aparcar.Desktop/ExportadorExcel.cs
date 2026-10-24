using System;
using System.IO;
using Aparcar.Domain;
using ClosedXML.Excel;

namespace Aparcar.Desktop;

/// <summary>Exportación real .xlsx. El resumen usa los cobros y devoluciones ya filtrados.</summary>
internal static class ExportadorExcel
{
    public static string Guardar(ResumenRecaudacion reporte,DateTime desde,DateTime hasta)
    {
        ArgumentNullException.ThrowIfNull(reporte);
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Aparcar","Reportes");
        Directory.CreateDirectory(folder);
        var ruta=Path.Combine(folder,
            $"Aparcar_Recaudacion_{desde:yyyyMMdd}_{hasta:yyyyMMdd}_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}.xlsx");
        using var libro=new XLWorkbook();
        var hoja=libro.Worksheets.Add("Recaudación");
        hoja.Cell(1,1).Value="APARCAR - RECAUDACIÓN (NO FISCAL)";
        hoja.Range(1,1,1,6).Merge();
        hoja.Range(1,1,1,6).Style.Font.Bold=true;
        hoja.Range(1,1,1,6).Style.Font.FontSize=15;
        hoja.Cell(2,1).Value="Desde:";
        hoja.Cell(2,2).Value=desde;
        hoja.Cell(2,2).Style.DateFormat.Format="dd/mm/yyyy";
        hoja.Cell(2,3).Value="Hasta:";
        hoja.Cell(2,4).Value=hasta;
        hoja.Cell(2,4).Style.DateFormat.Format="dd/mm/yyyy";
        string[] titulos={"Fecha y hora","Ticket","Patente","Medio","Importe (ARS)","Operador"};
        for(int col=1;col<=titulos.Length;col++) hoja.Cell(4,col).Value=titulos[col-1];
        var titulo=hoja.Range(4,1,4,6);
        titulo.Style.Font.Bold=true;
        titulo.Style.Fill.BackgroundColor=XLColor.FromHtml("#164677");
        titulo.Style.Font.FontColor=XLColor.White;
        int fila=5;
        foreach(var registro in reporte.Lineas)
        {
            hoja.Cell(fila,1).Value=registro.FechaUtc.ToLocalTime().DateTime;
            hoja.Cell(fila,1).Style.DateFormat.Format="dd/mm/yyyy hh:mm";
            hoja.Cell(fila,2).Value=registro.Ticket;
            hoja.Cell(fila,3).Value=registro.Patente;
            hoja.Cell(fila,4).Value=registro.Devuelto?"Devolución efectivo":registro.MedioPago;
            hoja.Cell(fila,5).Value=registro.ImporteCentavos/100m;
            hoja.Cell(fila,5).Style.NumberFormat.Format="#,##0.00;[Red]-#,##0.00";
            hoja.Cell(fila,6).Value=registro.Operador;
            fila++;
        }
        if(fila>5) hoja.Range(4,1,fila-1,6).SetAutoFilter();
        fila+=2;
        void Resumen(string tituloResumen,long centavos)
        {
            hoja.Cell(fila,4).Value=tituloResumen;
            hoja.Cell(fila,5).Value=centavos/100m;
            hoja.Cell(fila,5).Style.NumberFormat.Format="#,##0.00;[Red]-#,##0.00";
            fila++;
        }
        Resumen("Cobros brutos",reporte.CobrosBrutosCentavos);
        Resumen("Devoluciones",reporte.DevolucionesCentavos);
        Resumen("Cobros netos",reporte.CobrosNetosCentavos);
        Resumen("Efectivo neto",reporte.EfectivoCentavos);
        Resumen("Otros medios",reporte.ElectronicosCentavos);
        hoja.Columns(1,6).AdjustToContents();
        hoja.Column(4).Width=Math.Max(hoja.Column(4).Width,22);
        hoja.Column(6).Width=Math.Min(hoja.Column(6).Width,42);
        hoja.SheetView.FreezeRows(4);
        libro.SaveAs(ruta);
        return ruta;
    }
}
