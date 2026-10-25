using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class FiscalV12Tests
{
    private static (AparcarDb Db,string Dir) Crear()
    {
        string dir=Path.Combine(Path.GetTempPath(),"AparcarFiscal12",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminFiscal","ClaveSegura2026");
        db.IniciarSesion("AdminFiscal","ClaveSegura2026");
        return(db,dir);
    }
    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }
    private static (Guid Cobro,ComprobanteFiscalWsfe Factura) Datos(AparcarDb db)
    {
        db.AbrirTurno("Cajero",0);
        var srv=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(srv.Id,"AA123BC","Operario","");
        var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid());
        var hoy=DateOnly.FromDateTime(DateTime.Now);
        var factura=new ComprobanteFiscalWsfe(3,6,1,hoy,2,96,12345678,5,
            121m,0m,100m,0m,0m,21m,new[]{new IvaFiscalWsfe(5,100m,21m)},
            hoy,hoy,hoy);
        return(cobro.Id,factura);
    }
    [Fact]
    public void NoPermiteDosSolicitudesParaUnCobro()
    {
        var(db,dir)=Crear();
        try
        {
            var(cobro,factura)=Datos(db);
            var solicitud=db.PrepararFacturaHomologacion("20123456786","Lavadero",cobro,factura);
            Assert.Equal("Preparada",solicitud.Estado);
            Assert.Throws<InvalidOperationException>(()=>db.PrepararFacturaHomologacion(
                "20123456786","Lavadero",cobro,factura with{Numero=2}));
            Assert.Single(db.ListarFacturasHomologacion());
        }
        finally{Limpiar(dir);}
    }
    [Fact]
    public void LuegoDeEnviarDebeConciliarAntesDeVolverAIntentar()
    {
        var(db,dir)=Crear();
        try
        {
            var(cobro,factura)=Datos(db);
            var solicitud=db.PrepararFacturaHomologacion("20123456786","Lavadero",cobro,factura);
            Assert.Equal("PorConciliar",db.MarcarEnvioHomologacion(solicitud.Id).Estado);
            Assert.Throws<InvalidOperationException>(()=>db.MarcarEnvioHomologacion(solicitud.Id));
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarConciliacionHomologacion(
                solicitud.Id,new ResultadoConsultaWsfe(false,false,false,null,null,"Sin datos")));
            Assert.Equal("PorConciliar",db.ObtenerFacturaHomologacion(solicitud.Id).Estado);
            var aprobado=db.RegistrarConciliacionHomologacion(solicitud.Id,
                new ResultadoConsultaWsfe(true,true,true,"71234567890123",
                    DateOnly.FromDateTime(DateTime.Today.AddDays(10)),"OK"));
            Assert.Equal("Autorizada",aprobado.Estado);
            Assert.Equal("71234567890123",aprobado.Cae);
        }
        finally{Limpiar(dir);}
    }
}
