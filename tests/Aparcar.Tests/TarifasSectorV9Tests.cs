using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class TarifasSectorV9Tests
{
    private static (AparcarDb Db,string Ruta) Preparar()
    {
        var carpeta=Path.Combine(Path.GetTempPath(),"AparcarTarifasV9",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var db=new AparcarDb(Path.Combine(carpeta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminV9","ClaveSegura2026");
        db.IniciarSesion("AdminV9","ClaveSegura2026");
        return(db,carpeta);
    }
    private static void Limpiar(string ruta)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(ruta,true);
    }

    [Fact]
    public void HorarioNocturnoIdentificaAmbosLadosDeMedianoche()
    {
        var desde=new TimeOnly(20,0);var hasta=new TimeOnly(8,0);
        Assert.Equal(FranjasTarifarias.Diurna,FranjasTarifarias.Elegir(new TimeOnly(8,0),desde,hasta));
        Assert.Equal(FranjasTarifarias.Nocturna,FranjasTarifarias.Elegir(new TimeOnly(20,0),desde,hasta));
        Assert.Equal(FranjasTarifarias.Nocturna,FranjasTarifarias.Elegir(new TimeOnly(2,30),desde,hasta));
        Assert.Equal(FranjasTarifarias.Diurna,FranjasTarifarias.Elegir(new TimeOnly(19,59),desde,hasta));
        Assert.Equal(FranjasTarifarias.Nocturna,FranjasTarifarias.Elegir(
            new TimeOnly(13,0),new TimeOnly(12,0),new TimeOnly(18,0)));
        Assert.Throws<ArgumentException>(()=>FranjasTarifarias.Elegir(
            new TimeOnly(12,0),new TimeOnly(8,0),new TimeOnly(8,0)));
    }

    [Fact]
    public void TarifaSectorSeCongelaAlIngreso_TrasCambioDeVersion()
    {
        var (db,ruta)=Preparar();
        try
        {
            db.GuardarSector("Techado",10,ModosSector.Libre);
            db.ConfigurarHorarioTarifario("Techado",new TimeOnly(0,1),new TimeOnly(0,0));
            // Se elige la franja correspondiente al reloj de prueba, sin exigir una zona horaria particular.
            var hora=TimeOnly.FromDateTime(DateTime.Now);
            var franja=FranjasTarifarias.Elegir(hora,new TimeOnly(0,1),new TimeOnly(0,0));
            var primero=db.GuardarTarifaSector("Techado","Auto",franja,350000,175000,30,2100000,5);
            var ticket1=db.RegistrarIngreso("AB111AA","Auto","Techado");
            var viejo=db.BuscarActiva(ticket1.ToString())!;
            var tarifaVigente=db.ObtenerTarifaDeEstadia(viejo.Id);
            Assert.Equal(primero.Id,tarifaVigente.Id);
            Assert.Equal(350000,tarifaVigente.PrimeraHoraCentavos);
            var segundo=db.GuardarTarifaSector("Techado","Auto",franja,500000,250000,30,3000000,5);
            Assert.Equal(2,segundo.Version);
            Assert.Equal(primero.Id,db.ObtenerTarifaDeEstadia(viejo.Id).Id);
            var ticket2=db.RegistrarIngreso("CD222BB","Auto","Techado");
            var nuevo=db.BuscarActiva(ticket2.ToString())!;
            Assert.Equal(segundo.Id,db.ObtenerTarifaDeEstadia(nuevo.Id).Id);
            Assert.Equal(500000,db.ObtenerTarifaDeEstadia(nuevo.Id).PrimeraHoraCentavos);
            Assert.Equal(200000,db.ListarTarifas().Single(x=>x.TipoVehiculo=="Auto").PrimeraHoraCentavos);
        }
        finally{Limpiar(ruta);}
    }

    [Fact]
    public void SectorSinTarifaPropiaUsaTarifaGeneralVersionada()
    {
        var (db,ruta)=Preparar();
        try
        {
            db.GuardarSector("Descubierto",5,ModosSector.Libre);
            var ticket=db.RegistrarIngreso("EF333CC","Moto","Descubierto");
            var estadia=db.BuscarActiva(ticket.ToString())!;
            var congelada=db.ObtenerTarifaDeEstadia(estadia.Id);
            var nueva=db.CambiarTarifa("Moto",300000,100000,30,1000000,10);
            Assert.True(nueva.Version>congelada.Version);
            Assert.Equal(congelada.Id,db.ObtenerTarifaDeEstadia(estadia.Id).Id);
        }
        finally{Limpiar(ruta);}
    }

    [Fact]
    public void HorarioYSectorSoloSePuedenModificarConPermiso()
    {
        var (db,ruta)=Preparar();
        try
        {
            db.GuardarSector("VIP",5,ModosSector.Libre);
            Assert.Throws<ArgumentException>(()=>db.ConfigurarHorarioTarifario(
                "VIP",new TimeOnly(8,0),new TimeOnly(8,0)));
            db.ConfigurarHorarioTarifario("VIP",new TimeOnly(21,0),new TimeOnly(7,0));
            Assert.Equal(new TimeOnly(21,0),db.ObtenerHorarioTarifario("VIP").InicioNoche);
            db.CrearUsuario("CajeroV9","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("CajeroV9","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>db.GuardarTarifaSector(
                "VIP","Auto",FranjasTarifarias.Diurna,200000,100000,30,1200000,5));
            Assert.Throws<UnauthorizedAccessException>(()=>db.ConfigurarHorarioTarifario(
                "VIP",new TimeOnly(20,0),new TimeOnly(8,0)));
        }
        finally{Limpiar(ruta);}
    }
}
