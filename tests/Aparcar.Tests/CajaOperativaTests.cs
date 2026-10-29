using System;
using System.IO;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class CajaOperativaTests
{
    private static (AparcarDb Db,string Dir) NuevaBase()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminPrueba", "ClavePrueba123");
        db.IniciarSesion("AdminPrueba", "ClavePrueba123");
        return (db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static void RetrocederIngreso(AparcarDb db, Guid id, int minutos)
    {
        using var conexion=new SqliteConnection($"Data Source={db.RutaDb}");
        conexion.Open();
        using var cmd=conexion.CreateCommand();
        cmd.CommandText="UPDATE Estadias SET IngresoUtc=$fecha WHERE Id=$id";
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.AddMinutes(-minutos).ToString("O"));
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        Assert.Equal(1,cmd.ExecuteNonQuery());
    }

    [Fact]
    public void CobroEfectivo_NoPermiteDuplicados_Y_CierraCajaConArqueo()
    {
        var (db,dir)=NuevaBase();
        try
        {
            var ticket=db.RegistrarIngreso("AB123CD","Auto","General");
            var estadia=db.BuscarActiva(ticket.ToString())!;
            RetrocederIngreso(db,estadia.Id,80);
            Assert.Throws<InvalidOperationException>(()=>
                db.ConfirmarEfectivo(db.CotizarSalida(estadia.Id),0,Guid.NewGuid()));

            var turno=db.AbrirTurno("Operador 01",500_000);
            Assert.Throws<InvalidOperationException>(()=>db.AbrirTurno("Otro",0));
            var cotizacion=db.CotizarSalida(estadia.Id);
            Assert.Equal(turno.Id,cotizacion.TurnoId);
            Assert.Equal(300_000,cotizacion.ImporteCentavos);
            Assert.Throws<InvalidOperationException>(()=>
                db.ConfirmarEfectivo(cotizacion,200_000,Guid.NewGuid()));
            Assert.Single(db.ListarActivas());
            Assert.Empty(db.ListarCobros());

            var operacion=Guid.NewGuid();
            var cobro=db.ConfirmarEfectivo(cotizacion,400_000,operacion);
            Assert.Equal(100_000,cobro.VueltoCentavos);
            var repetido=db.ConfirmarEfectivo(cotizacion,400_000,operacion);
            Assert.Equal(cobro.Id,repetido.Id);
            Assert.Single(db.ListarCobros());
            Assert.Empty(db.ListarActivas());
            Assert.Throws<InvalidOperationException>(()=>
                db.ConfirmarEfectivo(cotizacion,400_000,Guid.NewGuid()));

            var abierto=db.ObtenerTurnoAbierto()!;
            Assert.Equal(300_000,abierto.CobradoCentavos);
            Assert.Equal(800_000,abierto.EsperadoCentavos);
            var arqueo=db.CerrarTurno(abierto.Id,795_000);
            Assert.Equal(-5_000,arqueo.DiferenciaCentavos);
            Assert.Null(db.ObtenerTurnoAbierto());
            Assert.Throws<InvalidOperationException>(()=>
                db.CerrarTurno(abierto.Id,795_000));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void CambioDeTarifa_RespetaVersionEnIngresoAnterior()
    {
        var (db,dir)=NuevaBase();
        try
        {
            var viejo=db.RegistrarIngreso("AA111AA","Auto","General");
            var estadiaVieja=db.BuscarActiva(viejo.ToString())!;
            RetrocederIngreso(db,estadiaVieja.Id,80);
            var original=db.CotizarSalida(estadiaVieja.Id);
            Assert.Equal(300_000,original.ImporteCentavos);
            Assert.Equal(1,original.TarifaVersion);

            var nueva=db.CambiarTarifa("Auto",500_000,200_000,30,2_000_000,5);
            Assert.Equal(2,nueva.Version);
            var otra=db.RegistrarIngreso("BB222BB","Auto","General");
            var estadiaNueva=db.BuscarActiva(otra.ToString())!;
            RetrocederIngreso(db,estadiaNueva.Id,80);
            Assert.Equal(300_000,db.CotizarSalida(estadiaVieja.Id).ImporteCentavos);
            var cotizacionNueva=db.CotizarSalida(estadiaNueva.Id);
            Assert.Equal(700_000,cotizacionNueva.ImporteCentavos);
            Assert.Equal(2,cotizacionNueva.TarifaVersion);
            Assert.Throws<ArgumentException>(()=>db.CambiarTarifa("Auto",200_000,1000,30,100_000,10));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void ReiniciarBase_NoBorraTurnoNiCobro()
    {
        var (db,dir)=NuevaBase();
        try
        {
            db.AbrirTurno("Cajero",0);
            var n=db.RegistrarIngreso("BB999BB","Moto","General");
            var estadia=db.BuscarActiva(n.ToString())!;
            RetrocederIngreso(db,estadia.Id,70);
            var c=db.CotizarSalida(estadia.Id);
            db.ConfirmarEfectivo(c,c.ImporteCentavos,Guid.NewGuid());
            var reabierta=new AparcarDb(db.RutaDb);
            reabierta.Inicializar();
            reabierta.IniciarSesion("AdminPrueba", "ClavePrueba123");
            Assert.Single(reabierta.ListarCobros());
            Assert.NotNull(reabierta.ObtenerTurnoAbierto());
            Assert.Empty(reabierta.ListarActivas());
        }
        finally { Limpiar(dir); }
    }
}
