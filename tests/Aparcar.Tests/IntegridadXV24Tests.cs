using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

/// <summary>
/// Una impresión enviada al spooler no garantiza entrega física.
/// Tras el primer intento ORIGINAL, cualquier recuperación será COPIA auditada.
/// </summary>
public sealed class IntegridadXV24Tests
{
    private static (AparcarDb Db,string Ruta) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarOriginalX24",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminX24","ClaveSegura2026");
        db.IniciarSesion("AdminX24","ClaveSegura2026");
        db.AbrirTurno("Cajero",0);
        return(db,dir);
    }

    private static TicketX Emitir(AparcarDb db)
    {
        db.CrearTalonarioX("0001","Caja Principal");
        var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
        var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid());
        return db.EmitirTicketX("0001","Lavadero",cobro.Id).Ticket;
    }

    private static void Limpiar(string ruta)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(ruta,true);
    }

    [Theory]
    [InlineData("Pendiente")]
    [InlineData("Enviado")]
    [InlineData("Error")]
    public void OriginalNoSeRepiteAunqueImpresoraFalle(string estado)
    {
        var(db,dir)=Preparar();
        try
        {
            var ticket=Emitir(db);
            Assert.False(db.TicketXTieneIntentoOriginal(ticket.Id));
            var original=db.RegistrarIntentoImpresionX(
                ticket.Id,false,"","Impresora ficticia");
            Assert.True(db.TicketXTieneIntentoOriginal(ticket.Id));
            if(estado!="Pendiente")
                db.RegistrarResultadoImpresionX(original,estado=="Enviado",
                    "El controlador devolvió estado simulado");
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarIntentoImpresionX(ticket.Id,false,"","Impresora ficticia"));

            var copia=db.RegistrarIntentoImpresionX(
                ticket.Id,true,"Recuperación de documento","Impresora ficticia");
            db.RegistrarResultadoImpresionX(copia,true,"Copia enviada al spooler");
            Assert.Equal(1,db.ListarTicketsX().Count);
            Assert.Equal(1,db.ListarTalonariosX().Single().UltimoNumero);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void TriggerSqliteProtegeOriginalContraAccesoDirecto()
    {
        var(db,dir)=Preparar();
        try
        {
            var ticket=Emitir(db);
            _=db.RegistrarIntentoImpresionX(ticket.Id,false,"","Impresora ficticia");
            using var conn=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=db.RutaDb}.ToString());
            conn.Open();
            using var cmd=conn.CreateCommand();
            cmd.CommandText="""
                INSERT INTO ImpresionesX
                    (Id,TicketXId,EsCopia,Motivo,Impresora,Estado,Detalle,CreadaUtc)
                VALUES($id,$ticket,0,'','Acceso directo','Pendiente','', $fecha);
                """;
            cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$ticket",ticket.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            var ex=Assert.Throws<SqliteException>(()=>cmd.ExecuteNonQuery());
            Assert.Equal(19,ex.SqliteErrorCode);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void MigracionConservaIntentosOriginalesHistoricosRepetidos()
    {
        var(db,dir)=Preparar();
        try
        {
            var ticket=Emitir(db);
            using(var conn=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=db.RutaDb}.ToString()))
            {
                conn.Open();
                using(var alterar=conn.CreateCommand())
                {
                    alterar.CommandText="""
                        DROP TRIGGER TR_TicketX_OriginalUnico;
                        DELETE FROM EsquemaVersion WHERE Numero=18;
                        """;
                    alterar.ExecuteNonQuery();
                }
                for(int i=0;i<2;i++)
                {
                    using var registrar=conn.CreateCommand();
                    registrar.CommandText="""
                        INSERT INTO ImpresionesX
                            (Id,TicketXId,EsCopia,Motivo,Impresora,Estado,Detalle,CreadaUtc)
                        VALUES($id,$ticket,0,'','Histórica','Enviado','', $fecha);
                        """;
                    registrar.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
                    registrar.Parameters.AddWithValue("$ticket",ticket.Id.ToString("D"));
                    registrar.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
                    registrar.ExecuteNonQuery();
                }
            }
            // La migración a esquema 18 se reinstala sin borrar ninguna fila previa.
            db.Inicializar();
            using(var conn=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=db.RutaDb}.ToString()))
            {
                conn.Open();
                using var cuenta=conn.CreateCommand();
                cuenta.CommandText="""
                    SELECT COUNT(*) FROM ImpresionesX
                    WHERE TicketXId=$ticket AND EsCopia=0;
                    """;
                cuenta.Parameters.AddWithValue("$ticket",ticket.Id.ToString("D"));
                Assert.Equal(2,Convert.ToInt32(cuenta.ExecuteScalar()));
                using var insertar=conn.CreateCommand();
                insertar.CommandText="""
                    INSERT INTO ImpresionesX
                        (Id,TicketXId,EsCopia,Motivo,Impresora,Estado,Detalle,CreadaUtc)
                    VALUES($id,$ticket,0,'','Nueva','Pendiente','',$fecha);
                    """;
                insertar.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
                insertar.Parameters.AddWithValue("$ticket",ticket.Id.ToString("D"));
                insertar.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
                Assert.Throws<SqliteException>(()=>insertar.ExecuteNonQuery());
            }
        }
        finally {Limpiar(dir);}
    }
}
