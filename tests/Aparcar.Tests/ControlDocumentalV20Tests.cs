using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class ControlDocumentalV20Tests
{
    private static (AparcarDb Db,string Ruta) Preparar()
    {
        var ruta=Path.Combine(Path.GetTempPath(),"AparcarControl20",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ruta);
        var db=new AparcarDb(Path.Combine(ruta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminControl","ClaveSegura2026");
        db.IniciarSesion("AdminControl","ClaveSegura2026");
        db.AbrirTurno("Cajero",0);
        return(db,ruta);
    }

    private static Guid CobrarLavadero(AparcarDb db)
    {
        var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
        return db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid()).Id;
    }

    [Fact]
    public void TicketXNoSustituyeLaFacturaNiModificaEstadoFiscal()
    {
        var(db,ruta)=Preparar();
        try
        {
            var cobro=CobrarLavadero(db);
            var previo=db.ListarEstadoDocumentalCobros().Single();
            Assert.Equal(cobro,previo.CobroId);
            Assert.Equal("PendienteDatosFiscales",previo.EstadoSeguimiento);
            Assert.False(previo.TieneTicketX);
            Assert.Null(previo.EstadoHomologacion);

            db.CrearTalonarioX("0001","Caja central");
            db.EmitirTicketX("0001","Lavadero",cobro);
            var despues=db.ListarEstadoDocumentalCobros().Single();
            Assert.True(despues.TieneTicketX);
            Assert.Equal("0001",despues.TalonarioX);
            Assert.Equal(1,despues.NumeroX);
            Assert.Equal("PendienteDatosFiscales",despues.EstadoSeguimiento);
            Assert.Null(despues.EstadoHomologacion);
            Assert.False(despues.EsHomologacionAutorizada);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(ruta,true);
        }
    }

    [Fact]
    public void HomologacionAutorizadaPermaneceDistinguidaDeFacturaProductiva()
    {
        var(db,ruta)=Preparar();
        try
        {
            var cobro=CobrarLavadero(db);
            var hoy=DateOnly.FromDateTime(DateTime.Today);
            var factura=new ComprobanteFiscalWsfe(3,6,1,hoy,2,96,12345678,5,
                121m,0m,100m,0m,0m,21m,
                new[]{new IvaFiscalWsfe(5,100m,21m)},hoy,hoy,hoy);
            var solicitud=db.PrepararFacturaHomologacion(
                "20123456786","Lavadero",cobro,factura);
            Assert.Equal("Preparada",db.ListarEstadoDocumentalCobros().Single().EstadoHomologacion);
            db.MarcarEnvioHomologacion(solicitud.Id);
            Assert.Equal("PorConciliar",db.ListarEstadoDocumentalCobros().Single().EstadoHomologacion);
            db.RegistrarConciliacionHomologacion(solicitud.Id,
                new ResultadoConsultaWsfe(true,true,true,"71234567890123",
                    hoy.AddDays(10),"OK"));
            var estado=db.ListarEstadoDocumentalCobros().Single();
            Assert.True(estado.EsHomologacionAutorizada);
            Assert.Equal("Autorizada",estado.EstadoHomologacion);
            Assert.Equal("PendienteDatosFiscales",estado.EstadoSeguimiento);
            Assert.False(estado.TieneTicketX);
            Assert.Equal(12100,estado.ImporteCentavos);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(ruta,true);
        }
    }

    [Fact]
    public void DevolucionRealSeReflejaAunqueExistaTicketX()
    {
        var(db,ruta)=Preparar();
        try
        {
            var cobro=CobrarLavadero(db);
            db.CrearTalonarioX("0001","Caja central");
            db.EmitirTicketX("0001","Lavadero",cobro);
            using(var cn=new SqliteConnection(
                new SqliteConnectionStringBuilder{DataSource=db.RutaDb}.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="""
                    INSERT INTO DevolucionesComplementarias(
                        Id,CobroId,TurnoId,Categoria,ReferenciaId,ImporteCentavos,
                        FechaUtc,Motivo,AutorizadoPor)
                    SELECT $id,c.Id,c.TurnoCajaId,c.Categoria,c.ReferenciaId,
                        c.ImporteCentavos,$fecha,'Devolucion de prueba','Supervisor'
                    FROM CobrosComplementarios c WHERE c.Id=$cobro;
                    """;
                cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$cobro",cobro.ToString("D"));
                cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            var estado=db.ListarEstadoDocumentalCobros().Single();
            Assert.True(estado.TieneTicketX);
            Assert.True(estado.Devuelto);
            Assert.Throws<ArgumentOutOfRangeException>(()=>db.ListarEstadoDocumentalCobros(0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(ruta,true);
        }
    }
}
