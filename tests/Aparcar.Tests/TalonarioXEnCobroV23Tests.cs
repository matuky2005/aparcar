using System;
using System.IO;
using System.Linq;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

/// <summary>
/// v0.23 — Talonario X predeterminado: elegir X es OPTATIVO en cada cobro.
/// La preferencia jamás autoriza duplicar un pago ni saltear ARCA.
/// </summary>
public sealed class TalonarioXEnCobroV23Tests
{
    private static (AparcarDb Db,string Directorio) Crear()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarX23",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminX23","ClaveSegura2026");
        db.IniciarSesion("AdminX23","ClaveSegura2026");
        return(db,dir);
    }

    [Fact]
    public void NoExistePreferenciaImplicitaSinSeleccionDelAdministrador()
    {
        var(db,dir)=Crear();
        try
        {
            Assert.Null(db.ObtenerTalonarioXParaCobros());
            db.CrearTalonarioX("0001","Caja Principal");
            // Un talonario existente no se convierte en default silenciosamente.
            Assert.Null(db.ObtenerTalonarioXParaCobros());
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }

    [Fact]
    public void GuardaYRecuperaElTalonarioElegidoSinCambiarNumeraciones()
    {
        var(db,dir)=Crear();
        try
        {
            db.CrearTalonarioX("0001","Caja Principal");
            db.CrearTalonarioX("0002","Caja Secundaria");
            var primero=db.ConfigurarTalonarioXParaCobros("0001");
            Assert.Equal("0001",primero.Codigo);
            Assert.Equal(0,primero.UltimoNumero);
            Assert.Equal("0001",db.ObtenerTalonarioXParaCobros()!.Codigo);

            var segundo=db.ConfigurarTalonarioXParaCobros("0002");
            Assert.Equal("0002",segundo.Codigo);
            Assert.Equal("0002",db.ObtenerTalonarioXParaCobros()!.Codigo);
            Assert.All(db.ListarTalonariosX(),x=>Assert.Equal(0,x.UltimoNumero));
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }

    [Fact]
    public void NoPuedeSeleccionarTalonarioInexistenteNiCodigoInvalido()
    {
        var(db,dir)=Crear();
        try
        {
            Assert.Throws<InvalidOperationException>(()=>
                db.ConfigurarTalonarioXParaCobros("0001"));
            Assert.Throws<ArgumentException>(()=>
                db.ConfigurarTalonarioXParaCobros("A001"));
            Assert.Null(db.ObtenerTalonarioXParaCobros());
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }

    [Fact]
    public void LosDatosGuardadosSobrevivenLaReaperturaDeLaDb()
    {
        var(db,dir)=Crear();
        try
        {
            db.CrearTalonarioX("0001","Caja Principal");
            db.ConfigurarTalonarioXParaCobros("0001");
            var otraInstancia=new AparcarDb(db.RutaDb);
            // No se fuerza una migración al reabrir una base existente.
            otraInstancia.Inicializar();
            otraInstancia.IniciarSesion("AdminX23","ClaveSegura2026");
            Assert.Equal("0001",otraInstancia.ObtenerTalonarioXParaCobros()!.Codigo);
            Assert.Single(otraInstancia.ListarTalonariosX());
        }
        finally {SqliteConnection.ClearAllPools();Directory.Delete(dir,true);}
    }
}
