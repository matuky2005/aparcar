using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class PanelOperativoV22Tests
{
    private static (AparcarDb Db,string Dir) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarPanel22",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminPanel","ClaveSegura2026");
        db.IniciarSesion("AdminPanel","ClaveSegura2026");
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    [Fact]
    public void PanelInicialNoAbreCajaNiMuestraSaldos()
    {
        var(db,dir)=Preparar();
        try
        {
            var antes=db.ObtenerPanelOperativoV22(12);
            Assert.False(antes.CajaAbierta);
            Assert.Equal(0,antes.Presentes);
            Assert.Equal(1000,antes.CapacidadHabilitada);
            Assert.Equal(1000,antes.LibresHabilitados);
            Assert.Empty(antes.UltimosIngresos);
            Assert.Empty(antes.UltimasSalidas);
            Assert.Throws<ArgumentOutOfRangeException>(()=>db.ObtenerPanelOperativoV22(0));
            Assert.Throws<ArgumentException>(()=>db.BuscarPresentesRapidoV22(""));
            Assert.Throws<ArgumentOutOfRangeException>(()=>db.BuscarPresentesRapidoV22("AA",0));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void BuscaPorPrefijoONumeroSinModificarLasEstadias()
    {
        var(db,dir)=Preparar();
        try
        {
            long a=db.RegistrarIngreso("AA123BC","Auto","General");
            long b=db.RegistrarIngreso("AA456BC","Moto","General");
            db.RegistrarIngreso("ZZ999XX","Auto","General");
            Assert.Equal(3,db.ObtenerPanelOperativoV22().Presentes);
            var prefijo=db.BuscarPresentesRapidoV22("aa");
            Assert.Equal(2,prefijo.Count);
            Assert.All(prefijo,x=>Assert.StartsWith("AA",x.Patente));
            var exacto=db.BuscarPresentesRapidoV22(a.ToString());
            Assert.Equal(a,exacto[0].Ticket);
            Assert.Equal("AA123BC",db.BuscarPresentesRapidoV22("AA123BC").Single().Patente);
            Assert.Empty(db.BuscarPresentesRapidoV22("HH"));
            Assert.Equal(3,db.ListarActivas().Count);
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void RespetaDisponibilidadRealDeSectoresAsignados()
    {
        var(db,dir)=Preparar();
        try
        {
            db.GuardarSector("Reservado",2,ModosSector.Asignado);
            db.GuardarPlaza("Reservado","R1",true);
            db.GuardarPlaza("Reservado","R2",true);
            db.RegistrarIngreso("AC123BC","Auto","Reservado","R1");
            var panel=db.ObtenerPanelOperativoV22();
            var sector=panel.Sectores.Single(x=>x.Nombre=="Reservado");
            Assert.Equal(1,sector.Ocupados);
            Assert.Equal(1,sector.Libres);
            Assert.Equal(1002,panel.CapacidadHabilitada);
            Assert.Equal(1001,panel.LibresHabilitados);
            Assert.Single(panel.UltimosIngresos);
            Assert.Equal(1,panel.Presentes);
            Assert.Equal(1,db.ListarActivas().Count);
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void SalidasRecientesSoloDespuesDeConfirmarElCobro()
    {
        var(db,dir)=Preparar();
        try
        {
            db.AbrirTurno("Cajero",0);
            var ticket=db.RegistrarIngreso("AD123BC","Auto","General");
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
            var previo=db.ObtenerPanelOperativoV22();
            Assert.True(previo.CajaAbierta);
            Assert.Equal("Cajero",previo.OperadorTurno);
            Assert.Empty(previo.UltimasSalidas);
            var cotizacion=db.CotizarSalida(estadia.Id);
            var cobro=db.ConfirmarPago(cotizacion,MediosPago.Efectivo,
                cotizacion.ImporteCentavos,"",false,Guid.NewGuid());
            var despues=db.ObtenerPanelOperativoV22();
            Assert.Equal(0,despues.Presentes);
            Assert.Empty(despues.UltimosIngresos);
            Assert.Equal(cobro.Ticket,despues.UltimasSalidas.Single().Ticket);
            Assert.Equal("AD123BC",despues.UltimasSalidas.Single().Patente);
            Assert.Empty(db.BuscarPresentesRapidoV22("AD"));
            Assert.Single(db.ListarCobros());
        }
        finally { Limpiar(dir); }
    }
}
