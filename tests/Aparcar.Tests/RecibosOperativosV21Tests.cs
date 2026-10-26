using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class RecibosOperativosV21Tests
{
    private static (AparcarDb Db,string Ruta) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarRecibos21",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminRecibo","ClaveSegura2026");
        db.IniciarSesion("AdminRecibo","ClaveSegura2026");
        db.AbrirTurno("Cajero",0);
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static CobroConfirmadoV4 Cobrar(AparcarDb db,string patente)
    {
        var ticket=db.RegistrarIngreso(patente,"Auto","General");
        var estadia=db.BuscarActiva(ticket.ToString())!;
        using(var cn=new SqliteConnection(
            new SqliteConnectionStringBuilder{DataSource=db.RutaDb}.ToString()))
        {
            cn.Open();
            using var cmd=cn.CreateCommand();
            cmd.CommandText="UPDATE Estadias SET IngresoUtc=$fecha WHERE Id=$id";
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.AddMinutes(-80).ToString("O"));
            cmd.Parameters.AddWithValue("$id",estadia.Id.ToString("D"));
            Assert.Equal(1,cmd.ExecuteNonQuery());
        }
        var precio=db.CotizarSalida(estadia.Id);
        return db.ConfirmarPago(precio,MediosPago.Efectivo,precio.ImporteCentavos,
            "",false,Guid.NewGuid());
    }

    [Fact]
    public void RegistrarOriginalNoRepiteCobroNiAdmiteOtroOriginal()
    {
        var(db,dir)=Preparar();
        try
        {
            var cobro=Cobrar(db,"AA123BC");
            var antes=db.ObtenerResumenCaja()!;
            var intento=db.RegistrarIntentoReciboCobroV21(
                cobro.Id,false,"","Impresora Simulada");
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIntentoReciboCobroV21(
                cobro.Id,false,"","Impresora Simulada"));
            Assert.True(db.ObtenerReciboCobroV21(cobro.Id).TieneIntentoOriginal);
            db.RegistrarResultadoReciboCobroV21(intento,true,"Enviado al spooler simulado");
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarResultadoReciboCobroV21(intento,false,"Repetido"));
            var despues=db.ObtenerResumenCaja()!;
            Assert.Equal(antes.EfectivoEsperadoCentavos,despues.EfectivoEsperadoCentavos);
            Assert.Equal("Enviado",db.ObtenerReciboCobroV21(cobro.Id).EstadoUltimaImpresion);
            Assert.Single(db.ListarRecibosCobroV21());
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void FalloNoBorraOriginalYCopiaConMotivoQuedaAuditada()
    {
        var(db,dir)=Preparar();
        try
        {
            var cobro=Cobrar(db,"AB987CD");
            var original=db.RegistrarIntentoReciboCobroV21(
                cobro.Id,false,"","Impresora sin papel");
            db.RegistrarResultadoReciboCobroV21(original,false,"No hay papel");
            Assert.Equal("Error",db.ObtenerReciboCobroV21(cobro.Id).EstadoUltimaImpresion);
            Assert.Throws<ArgumentException>(()=>db.RegistrarIntentoReciboCobroV21(
                cobro.Id,true,"corto","Impresora Simulada"));
            var copia=db.RegistrarIntentoReciboCobroV21(cobro.Id,true,
                "Reimprimir por falla física","Impresora Simulada");
            db.RegistrarResultadoReciboCobroV21(copia,true,"Aceptado en simulación");
            var recibo=db.ObtenerReciboCobroV21(cobro.Id);
            Assert.True(recibo.TieneIntentoOriginal);
            Assert.Equal("Enviado",recibo.EstadoUltimaImpresion);
            using(var cn=new SqliteConnection(
                new SqliteConnectionStringBuilder{DataSource=db.RutaDb}.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="""
                    SELECT Motivo,EsCopia,Estado FROM ImpresionesReciboCobro WHERE Id=$id
                    """;
                cmd.Parameters.AddWithValue("$id",copia.ToString("D"));
                using var r=cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Contains("falla",r.GetString(0));
                Assert.Equal(1,r.GetInt64(1));
                Assert.Equal("Enviado",r.GetString(2));
            }
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIntentoReciboCobroV21(
                cobro.Id,false,"","Impresora Simulada"));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void PersistenciaEntreReiniciosYDobleValidacion()
    {
        var(db,dir)=Preparar();
        try
        {
            var primero=Cobrar(db,"AC222BC");
            var segundo=Cobrar(db,"AD333BC");
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIntentoReciboCobroV21(
                Guid.NewGuid(),false,"","Impresora Simulada"));
            Assert.Throws<ArgumentException>(()=>db.RegistrarIntentoReciboCobroV21(
                primero.Id,false,"",""));
            Assert.Throws<ArgumentOutOfRangeException>(()=>db.ListarRecibosCobroV21(0));
            var id=db.RegistrarIntentoReciboCobroV21(
                segundo.Id,false,"","Impresora Simulada");
            var copiaDb=new AparcarDb(db.RutaDb);
            copiaDb.Inicializar();
            copiaDb.IniciarSesion("AdminRecibo","ClaveSegura2026");
            Assert.Equal(2,copiaDb.ListarRecibosCobroV21().Count);
            Assert.True(copiaDb.ObtenerReciboCobroV21(segundo.Id).TieneIntentoOriginal);
            Assert.Equal("Pendiente",copiaDb.ObtenerReciboCobroV21(segundo.Id).EstadoUltimaImpresion);
            copiaDb.RegistrarResultadoReciboCobroV21(id,false,"Resultado después del reinicio");
            Assert.Equal("Error",db.ObtenerReciboCobroV21(segundo.Id).EstadoUltimaImpresion);
        }
        finally { Limpiar(dir); }
    }
}
