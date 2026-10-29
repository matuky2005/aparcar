using System;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using Aparcar.Infrastructure;

namespace Aparcar.Desktop;

/// <summary>
/// Formato térmico no fiscal. No genera QR ARCA ni CAE.
/// "Enviado" es aceptación del spooler, no evidencia de impresión física.
/// </summary>
internal static class TicketXPrinter
{
    private static readonly CultureInfo Argentina=CultureInfo.GetCultureInfo("es-AR");

    public static void Imprimir(TicketX ticket,string impresora,int anchoMm,bool copia=false)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if(anchoMm is not (58 or 80))
            throw new ArgumentOutOfRangeException(nameof(anchoMm),"Solo papel térmico de 58 u 80 mm.");
        if(string.IsNullOrWhiteSpace(impresora))
            throw new InvalidOperationException("Configurá una impresora para tickets X.");
        using var documento=new PrintDocument();
        documento.PrinterSettings.PrinterName=impresora;
        if(!documento.PrinterSettings.IsValid)
            throw new InvalidOperationException("La impresora X no está disponible en Windows.");
        int ancho=anchoMm==58?228:315;
        documento.DocumentName=$"APARCAR X {ticket.Talonario}-{ticket.Numero:D8}"+(copia?" COPIA":"");
        documento.PrintController=new StandardPrintController();
        documento.DefaultPageSettings.PaperSize=new PaperSize("Ticket X no fiscal",ancho,700);
        documento.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        documento.PrintPage+=(_,e)=>
        {
            if(e.Graphics is null)
                throw new InvalidOperationException("No se obtuvo superficie de impresión.");
            var g=e.Graphics;
            g.PageUnit=GraphicsUnit.Display;
            g.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            g.Clear(Color.White);
            using var titulo=new Font("Arial",15f,FontStyle.Bold);
            using var fuerte=new Font("Arial",9.2f,FontStyle.Bold);
            using var normal=new Font("Arial",8f,FontStyle.Regular);
            using var centrado=new StringFormat{Alignment=StringAlignment.Center};
            float y=12;
            var margen=12;
            var area=ancho-2*margen;
            void Linea(string texto,Font fuente,bool centro=false)
            {
                float h=Math.Max(23,g.MeasureString(texto,fuente,area).Height+9);
                g.DrawString(texto,fuente,Brushes.Black,
                    new RectangleF(margen,y,area,h),
                    centro?centrado:StringFormat.GenericDefault);
                y+=h;
            }
            Linea("X",titulo,true);
            Linea("DOCUMENTO NO VALIDO COMO FACTURA",fuerte,true);
            Linea("APARCAR",titulo,true);
            Linea("TICKET X - CONTROL INTERNO",fuerte,true);
            Linea(copia?"COPIA - REIMPRESION":"ORIGINAL",fuerte,true);
            Linea($"X  {ticket.Talonario}-{ticket.Numero:D8}",fuerte,true);
            Linea($"Emitido: {ticket.EmitidoUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}",normal);
            Linea($"Operacion: {ticket.Origen}",normal);
            Linea($"Detalle: {ticket.Descripcion}",normal);
            Linea($"Cobrado: {ticket.FechaCobroUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}",normal);
            Linea($"Medio de pago: {ticket.MedioPago}",normal);
            Linea($"Importe: $ {(ticket.ImporteCentavos/100m).ToString("N2",Argentina)}",fuerte);
            if(ticket.Devuelto)
                Linea("COBRO DEVUELTO - CONSTANCIA HISTORICA",fuerte,true);
            y+=8;
            Linea("DOCUMENTO NO VALIDO COMO FACTURA",fuerte,true);
            Linea("No reemplaza la facturacion exigida por ARCA.",normal,true);
            e.HasMorePages=false;
        };
        documento.Print();
    }
}
