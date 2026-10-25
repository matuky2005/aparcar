using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class SectoresV8Tests
{
    private static (AparcarDb Db,string Carpeta) Preparar()
    {
        var carpeta=Path.Combine(Path.GetTempPath(),"AparcarSectores",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(carpeta);
        var db=new AparcarDb(Path.Combine(carpeta,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("SupervisorV8","ClaveSegura2026");
        db.IniciarSesion("SupervisorV8","ClaveSegura2026");
        return (db,carpeta);
    }

    private static void Limpiar(string carpeta)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(carpeta,true);
    }

    private static void Salir(AparcarDb db,long ticket)
    {
        if(db.ObtenerTurnoAbierto() is null)db.AbrirTurno("Caja",100_000);
        var estadia=db.BuscarActiva(ticket.ToString())!;
        var cotizacion=db.CotizarSalida(estadia.Id);
        db.ConfirmarPago(cotizacion,MediosPago.Efectivo,
            cotizacion.ImporteCentavos,"",false,Guid.NewGuid());
    }

    [Fact]
    public void SectorLibre_NuncaExcedeSuCapacidad_YLiberaAlSalir()
    {
        var (db,carpeta)=Preparar();
        try
        {
            db.GuardarSector("General",1,ModosSector.Libre);
            var ticket=db.RegistrarIngreso("AB111AA","Auto","General");
            Assert.Equal(0,db.ListarSectores().Single(x=>x.Nombre=="General").Libres);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarIngreso("BB222BB","Auto","General"));
            Assert.Throws<InvalidOperationException>(()=>
                db.GuardarSector("General",1,ModosSector.Asignado));
            Assert.Throws<InvalidOperationException>(()=>
                db.GuardarSector("General",1,ModosSector.Libre,false));
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarIngreso("BB222BB","Auto","NoExiste"));
            Salir(db,ticket);
            Assert.Equal(1,db.ListarSectores().Single(x=>x.Nombre=="General").Libres);
            Assert.True(db.RegistrarIngreso("BB222BB","Auto","General")>0);
            Assert.True(db.VerificarCadenaAuditoria());
        }
        finally{Limpiar(carpeta);}
    }

    [Fact]
    public void SectorAsignado_AsignaEnOrden_NoDuplica_YReutilizaPlaza()
    {
        var (db,carpeta)=Preparar();
        try
        {
            db.GuardarSector("Techado",2,ModosSector.Asignado);
            db.GuardarPlaza("Techado","A-01");
            db.GuardarPlaza("Techado","A-02");
            var t1=db.RegistrarIngreso("AA111AA","Auto","Techado","A-01");
            var t2=db.RegistrarIngreso("BB222BB","Moto","Techado");
            var e1=db.BuscarActiva(t1.ToString())!;
            var e2=db.BuscarActiva(t2.ToString())!;
            Assert.Equal("A-01",db.ConsultarUbicacion(e1.Id)!.Plaza);
            Assert.Equal("A-02",db.ConsultarUbicacion(e2.Id)!.Plaza);
            Assert.Throws<InvalidOperationException>(()=>
                db.RegistrarIngreso("CC333CC","Auto","Techado"));
            Assert.Throws<InvalidOperationException>(()=>db.GuardarPlaza("Techado","A-01",false));
            Assert.Throws<InvalidOperationException>(()=>db.GuardarSector("Techado",1,ModosSector.Asignado));
            Salir(db,t1);
            Assert.False(db.ListarPlazas("Techado").Single(x=>x.Codigo=="A-01").Ocupada);
            var t3=db.RegistrarIngreso("CC333CC","Auto","Techado");
            var e3=db.BuscarActiva(t3.ToString())!;
            Assert.Equal("A-01",db.ConsultarUbicacion(e3.Id)!.Plaza);
            Assert.Equal(0,db.ListarSectores().Single(x=>x.Nombre=="Techado").Libres);
            Assert.Equal(2,db.ListarPlazas("Techado").Count(x=>x.Ocupada));
        }
        finally{Limpiar(carpeta);}
    }

    [Fact]
    public void PlazaInhabilitadaNoSeAsigna_YNoPermitePlazaEnSectorLibre()
    {
        var (db,carpeta)=Preparar();
        try
        {
            db.GuardarSector("VIP",3,ModosSector.Asignado);
            db.GuardarPlaza("VIP","P01",false);
            db.GuardarPlaza("VIP","P02",true);
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIngreso("XY111AB","Auto","VIP","P01"));
            var t=db.RegistrarIngreso("XY111AB","Auto","VIP");
            Assert.Equal("P02",db.ConsultarUbicacion(db.BuscarActiva(t.ToString())!.Id)!.Plaza);
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIngreso("XZ222AB","Auto","VIP"));
            Assert.Throws<InvalidOperationException>(()=>db.RegistrarIngreso("XZ222AB","Auto","General","P01"));
        }
        finally{Limpiar(carpeta);}
    }

    [Fact]
    public void CajeroNoPuedeReconfigurarCapacidad_YReaperturaConservaSector()
    {
        var (db,carpeta)=Preparar();
        try
        {
            db.GuardarSector("MotoTechado",6,ModosSector.Libre);
            db.CrearUsuario("CajeroV8","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("CajeroV8","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>db.GuardarSector("MotoTechado",1,ModosSector.Libre));
            var ticket=db.RegistrarIngreso("ZZ999ZZ","Moto","MotoTechado");
            Assert.Equal("MotoTechado",db.BuscarActiva(ticket.ToString())!.Sector);
            db.CerrarSesion();
            var reabierta=new AparcarDb(db.RutaDb);
            reabierta.Inicializar();
            Assert.Throws<UnauthorizedAccessException>(()=>reabierta.ListarSectores());
            reabierta.IniciarSesion("SupervisorV8","ClaveSegura2026");
            var sector=reabierta.ListarSectores().Single(x=>x.Nombre=="MotoTechado");
            Assert.Equal(1,sector.Ocupados);
            Assert.Equal(5,sector.Libres);
        }
        finally{Limpiar(carpeta);}
    }
}
