using System;
using System.IO;
using System.Linq;
using Aparcar.Infrastructure;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class TalonariosXV19Tests
{
    private static (AparcarDb Db,string Ruta) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarX19",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminX","ClaveSegura2026");
        db.IniciarSesion("AdminX","ClaveSegura2026");
        db.AbrirTurno("Cajero",0);
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static Guid CobrarLavadero(AparcarDb db)
    {
        var servicio=db.GuardarServicioLavado("Lavado completo","Auto",12100);
        var orden=db.CrearOrdenLavado(servicio.Id,"AA123BC","Operario","");
        var cobro=db.CobrarComplemento("Lavadero",orden.Id,MediosPago.Efectivo,
            12100,"",false,Guid.NewGuid());
        return cobro.Id;
    }

    [Fact]
    public void NumeracionIndependientePorTalonarioYUnSoloXPorCobro()
    {
        var(db,dir)=Preparar();
        try
        {
            db.CrearTalonarioX("0001","Caja Central");
            db.CrearTalonarioX("0002","Caja Lavadero");
            var cobro1=CobrarLavadero(db);
            var result=db.EmitirTicketX("0001","Lavadero",cobro1);
            Assert.True(result.EsNuevo);
            Assert.Equal(1,result.Ticket.Numero);
            Assert.Equal(12100,result.Ticket.ImporteCentavos);
            Assert.Equal("Lavadero",result.Ticket.Origen);
            var repetido=db.EmitirTicketX("0001","Lavadero",cobro1);
            Assert.False(repetido.EsNuevo);
            Assert.Equal(result.Ticket.Id,repetido.Ticket.Id);
            Assert.Throws<InvalidOperationException>(()=>db.EmitirTicketX("0002","Lavadero",cobro1));
            var cobro2=CobrarLavadero(db);
            Assert.Equal(1,db.EmitirTicketX("0002","Lavadero",cobro2).Ticket.Numero);
            Assert.Equal(1,db.ListarTalonariosX().Single(x=>x.Codigo=="0001").UltimoNumero);
            Assert.Equal(1,db.ListarTalonariosX().Single(x=>x.Codigo=="0002").UltimoNumero);
            Assert.Equal(2,db.ListarTicketsX().Count);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void SeImprimeSinCrearNuevosIngresosYSeAuditaCopia()
    {
        var(db,dir)=Preparar();
        try
        {
            db.CrearTalonarioX("0001","Caja Central");
            var cobro=CobrarLavadero(db);
            var antes=db.ObtenerResumenCaja();
            var ticket=db.EmitirTicketX("0001","Lavadero",cobro).Ticket;
            var original=db.RegistrarIntentoImpresionX(ticket.Id,false,"","Impresora prueba");
            db.RegistrarResultadoImpresionX(original,true,"Enviado a spooler simulado");
            Assert.Throws<ArgumentException>(()=>db.RegistrarIntentoImpresionX(
                ticket.Id,true,"corto","Impresora prueba"));
            var copia=db.RegistrarIntentoImpresionX(
                ticket.Id,true,"Necesito otra copia","Impresora prueba");
            db.RegistrarResultadoImpresionX(copia,false,"Impresora sin papel");
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarResultadoImpresionX(copia,true,"Duplicado"));
            var despues=db.ObtenerResumenCaja();
            Assert.Equal(antes!.EfectivoEsperadoCentavos,despues!.EfectivoEsperadoCentavos);
            Assert.Equal(ticket.Id,db.ObtenerTicketX("0001",1).Id);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void RechazaCobroInexistenteYCodigoDeTalonarioRepetido()
    {
        var(db,dir)=Preparar();
        try
        {
            db.CrearTalonarioX("0001","Caja Central");
            Assert.Throws<InvalidOperationException>(()=>db.CrearTalonarioX("0001","Otro"));
            Assert.Throws<ArgumentException>(()=>db.CrearTalonarioX("123","Caja Central"));
            Assert.Throws<InvalidOperationException>(()=>
                db.EmitirTicketX("0001","Estacionamiento",Guid.NewGuid()));
            Assert.Equal(0,db.ListarTalonariosX().Single().UltimoNumero);
        }
        finally {Limpiar(dir);}
    }

    [Fact]
    public void SeRechazanCobrosDevueltosYSeMarcanXHistoricos()
    {
        var(db,dir)=Preparar();
        try
        {
            db.CrearTalonarioX("0001","Caja Central");
            var cobro=CobrarLavadero(db);
            var ticket=db.EmitirTicketX("0001","Lavadero",cobro).Ticket;
            Assert.False(ticket.Devuelto);
            // La devolución complementaria exige una autorización de supervisor:
            // simulamos el registro persistente preexistente para verificar que
            // la vista X deriva el estado del cobro, no lo congela como vigente.
            using(var cn=new SqliteConnection(
                new SqliteConnectionStringBuilder{DataSource=db.RutaDb}.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="""
                    INSERT INTO DevolucionesComplementarias(
                        Id,CobroId,TurnoId,Categoria,ReferenciaId,ImporteCentavos,
                        FechaUtc,Motivo,AutorizadoPor)
                    SELECT $id, c.Id,c.TurnoCajaId,c.Categoria,c.ReferenciaId,c.ImporteCentavos,
                        $fecha,'Devolución de prueba','Supervisor'
                    FROM CobrosComplementarios c WHERE c.Id=$cobro;
                    """;
                cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$cobro",cobro.ToString("D"));
                cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.True(db.ObtenerTicketX("0001",1).Devuelto);
            var segundo=CobrarLavadero(db);
            // No se permite emitir uno nuevo sobre un cobro devuelto.
            using(var cn=new SqliteConnection(
                new SqliteConnectionStringBuilder{DataSource=db.RutaDb}.ToString()))
            {
                cn.Open();
                using var cmd=cn.CreateCommand();
                cmd.CommandText="""
                    INSERT INTO DevolucionesComplementarias(
                        Id,CobroId,TurnoId,Categoria,ReferenciaId,ImporteCentavos,
                        FechaUtc,Motivo,AutorizadoPor)
                    SELECT $id, c.Id,c.TurnoCajaId,c.Categoria,c.ReferenciaId,c.ImporteCentavos,
                        $fecha,'Devolución de prueba','Supervisor'
                    FROM CobrosComplementarios c WHERE c.Id=$cobro;
                    """;
                cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
                cmd.Parameters.AddWithValue("$cobro",segundo.ToString("D"));
                cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.Throws<InvalidOperationException>(()=>db.EmitirTicketX("0001","Lavadero",segundo));
            Assert.Equal(1,db.ListarTalonariosX().Single().UltimoNumero);
        }
        finally {Limpiar(dir);}
    }
}
