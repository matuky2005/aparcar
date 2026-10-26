using System;
using System.Drawing;
using System.Drawing.Printing;
using Aparcar.Domain;
using QRCoder;

namespace Aparcar.Desktop;

/// <summary>
/// Impresión por controlador Windows (GDI). Compatible con impresoras que acepten
/// trabajos PrintDocument a través de su driver. No envía comandos ESC/POS.
/// </summary>
internal static class TicketPrinter
{
    public static void ImprimirIngreso(TicketIngreso ticket, string impresora, int anchoMm, bool duplicado=false)
    {
        if (string.IsNullOrWhiteSpace(impresora))
            throw new InvalidOperationException("Seleccioná una impresora en Configuración.");

        using var documento = new PrintDocument();
        documento.PrinterSettings.PrinterName = impresora;
        if (!documento.PrinterSettings.IsValid)
            throw new InvalidOperationException("La impresora elegida no está disponible en Windows.");

        var ancho = anchoMm == 58 ? 228 : 315;
        documento.DocumentName = $"Aparcar {(duplicado?"DUPLICADO ":"")}ingreso {ticket.Numero:D7}";
        documento.PrintController = new StandardPrintController(); // Sin diálogo automático.
        documento.DefaultPageSettings.PaperSize = new PaperSize("Ticket Aparcar", ancho, 750);
        documento.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        documento.PrintPage += (_, e) => PintarIngreso(e, ticket, ancho, duplicado);
        documento.Print();
    }

    /// <summary>Recibo no fiscal: acredita un cobro confirmado por el operador.</summary>
    public static void ImprimirCobro(CobroEfectivoConfirmado cobro,string impresora,int anchoMm,
        string medioPago = "Efectivo",bool copia=false,bool devuelto=false)
    {
        if (string.IsNullOrWhiteSpace(impresora))
            throw new InvalidOperationException("Seleccioná una impresora en Configuración.");
        using var documento=new PrintDocument();
        documento.PrinterSettings.PrinterName=impresora;
        if(!documento.PrinterSettings.IsValid)
            throw new InvalidOperationException("La impresora no está disponible en Windows.");
        int ancho=anchoMm==58 ? 228 : 315;
        documento.DocumentName=$"Aparcar cobro {cobro.Ticket:D7}"+(copia?" COPIA":" ORIGINAL");
        documento.PrintController=new StandardPrintController();
        documento.DefaultPageSettings.PaperSize=new PaperSize("Recibo Aparcar",ancho,550);
        documento.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        documento.PrintPage+=(_,e)=>
        {
            if(e.Graphics is null)throw new InvalidOperationException("No se puede dibujar el recibo.");
            var g=e.Graphics;
            g.PageUnit=GraphicsUnit.Display;
            g.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            g.Clear(Color.White);
            using var t=new Font("Arial",14f,FontStyle.Bold);
            using var b=new Font("Arial",10f,FontStyle.Bold);
            using var n=new Font("Arial",9f,FontStyle.Regular);
            using var c=new StringFormat { Alignment=StringAlignment.Center };
            float y=15;
            void Linea(string valor,Font fuente,int altura=26,bool centrado=false)
            {
                g.DrawString(valor,fuente,Brushes.Black,new RectangleF(12,y,ancho-24,altura),
                    centrado?c:StringFormat.GenericDefault);
                y+=altura;
            }
            Linea("APARCAR",t,34,true);
            Linea("COMPROBANTE NO FISCAL",b,30,true);
            Linea(copia?"COPIA - REIMPRESIÓN":"ORIGINAL",b,27,true);
            Linea($"TICKET #{cobro.Ticket:D7}",b);
            Linea($"FECHA: {cobro.FechaUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}",n);
            Linea($"MEDIO: {medioPago.ToUpperInvariant()}",n);
            Linea($"TOTAL: {(cobro.ImporteCentavos/100m):N2}",b);
            Linea($"RECIBIDO: {(cobro.EfectivoRecibidoCentavos/100m):N2}",n);
            Linea($"VUELTO: {(cobro.VueltoCentavos/100m):N2}",b);
            if(devuelto) Linea("COBRO DEVUELTO - DOCUMENTO HISTÓRICO",b,32,true);
            y+=8;
            Linea("Gracias por su visita.",n,25,true);
            Linea("No reemplaza factura ni ticket fiscal.",n,30,true);
            e.HasMorePages=false;
        };
        documento.Print();
    }

    private static void PintarIngreso(PrintPageEventArgs e, TicketIngreso ticket, int ancho, bool duplicado)
    {
        if (e.Graphics is null) throw new InvalidOperationException("El controlador no generó superficie de impresión.");

        var g = e.Graphics;
        g.PageUnit = GraphicsUnit.Display;
        g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
        g.Clear(Color.White);

        var centro = new StringFormat { Alignment = StringAlignment.Center };
        using var titulo = new Font("Arial", 14f, FontStyle.Bold);
        using var negrita = new Font("Arial", 10f, FontStyle.Bold);
        using var normal = new Font("Arial", 9f, FontStyle.Regular);
        using var pequeno = new Font("Arial", 7.5f, FontStyle.Regular);
        const int margen = 12;
        var usable = ancho - 2 * margen;
        float y = 14;

        void Linea(string texto, Font fuente, float alto = 25, bool centrado = false)
        {
            g.DrawString(texto, fuente, Brushes.Black,
                new RectangleF(margen, y, usable, alto),
                centrado ? centro : StringFormat.GenericDefault);
            y += alto;
        }

        Linea("APARCAR", titulo, 32, true);
        Linea("TICKET DE INGRESO", negrita, 26, true);
        if(duplicado) Linea("DUPLICADO - REIMPRESION", negrita, 32, true);
        Linea($"N° {ticket.Numero:D7}", negrita, 28, true);
        Linea($"PATENTE: {ticket.Patente}", normal);
        Linea($"VEHÍCULO: {ticket.TipoVehiculo}", normal);
        Linea($"SECTOR: {ticket.Sector}", normal);
        if(!string.IsNullOrWhiteSpace(ticket.Plaza))
            Linea($"PLAZA: {ticket.Plaza}", negrita);
        Linea($"FECHA: {ticket.FechaIngresoUtc.ToLocalTime():dd/MM/yyyy}", normal);
        Linea($"HORA: {ticket.FechaIngresoUtc.ToLocalTime():HH:mm:ss}", normal);
        y += 8;

        using var qrGenerator = new QRCodeGenerator();
        using var data = qrGenerator.CreateQrCode("APARCAR:" + ticket.TokenQr, QRCodeGenerator.ECCLevel.M);
        var filas = data.ModuleMatrix.Count;
        var dimension = Math.Min(usable - 12, ancho == 58 ? 155 : 185);
        var celda = (float)dimension / filas;
        var izquierda = (ancho - dimension) / 2f;
        using (var fondo = new SolidBrush(Color.White))
            g.FillRectangle(fondo, izquierda - 5, y - 5, dimension + 10, dimension + 10);
        for (int fila = 0; fila < filas; fila++)
            for (int col = 0; col < filas; col++)
                if (data.ModuleMatrix[fila][col])
                    g.FillRectangle(Brushes.Black,
                        izquierda + col * celda, y + fila * celda, celda + .2f, celda + .2f);
        y += dimension + 18;

        Linea("Escaneá este código en la salida.", pequeno, 25, true);
        Linea("Conservá el ticket durante tu estadía.", pequeno, 24, true);
        Linea("No es comprobante fiscal.", pequeno, 24, true);
        e.HasMorePages = false;
    }
}
