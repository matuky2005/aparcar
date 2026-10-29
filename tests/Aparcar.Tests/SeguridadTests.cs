using System;
using System.IO;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class SeguridadTests
{
    private static (AparcarDb Db,string Dir) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarSecurity",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"datos.db"));
        db.Inicializar();
        return(db,dir);
    }

    private static void Limpiar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    [Fact]
    public void PrimerAdministradorExigeAutenticacionYPermisos()
    {
        var (db,dir)=Preparar();
        try
        {
            Assert.False(db.HayAdministrador());
            Assert.Throws<ArgumentException>(()=>db.CrearAdministradorInicial("admin","1234"));
            var admin=db.CrearAdministradorInicial("admin","ClaveSegura2026");
            Assert.True(db.HayAdministrador());
            Assert.Equal(RolesAparcar.Administrador,admin.Rol);
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.RegistrarIngreso("AB123CD","Auto","General"));
            Assert.Throws<InvalidOperationException>(()=>
                db.CrearAdministradorInicial("admin2","ClaveSegura2026"));

            var sesion=db.IniciarSesion("ADMIN","ClaveSegura2026");
            Assert.Equal(RolesAparcar.Administrador,sesion.Rol);
            var cajero=db.CrearUsuario("cajero01","ClaveCajero2026",RolesAparcar.Cajero);
            Assert.Throws<InvalidOperationException>(()=>
                db.CrearUsuario("CAJERO01","ClaveDiferente2026",RolesAparcar.Cajero));
            Assert.Throws<InvalidOperationException>(()=>db.DesactivarUsuario(admin.Id));
            Assert.Throws<InvalidOperationException>(()=>
                db.CambiarRolUsuario(admin.Id,RolesAparcar.Consulta));
            db.CerrarSesion();

            var operador=db.IniciarSesion("cajero01","ClaveCajero2026");
            Assert.Equal(cajero.Id,operador.Id);
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.CambiarTarifa("Auto",200000,100000,30,1200000,10));
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.CrearUsuario("segundo","ClaveSegura2026",RolesAparcar.Administrador));
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.ListarAuditoria());
            Assert.True(db.RegistrarIngreso("AB123CD","Auto","General")>0);
            db.CerrarSesion();

            db.IniciarSesion("admin","ClaveSegura2026");
            db.DesactivarUsuario(cajero.Id);
            Assert.True(db.VerificarCadenaAuditoria());
            Assert.NotEmpty(db.ListarAuditoria());
            db.CerrarSesion();
            Assert.Throws<InvalidOperationException>(()=>
                db.IniciarSesion("cajero01","ClaveCajero2026"));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void CincoContraseñasIncorrectasBloqueanTemporalmenteLaCuenta()
    {
        var (db,dir)=Preparar();
        try
        {
            db.CrearAdministradorInicial("admin","ClaveSegura2026");
            for(int i=0;i<5;i++)
                Assert.Throws<InvalidOperationException>(()=>
                    db.IniciarSesion("admin","ClaveIncorrecta2026"));
            Assert.Throws<InvalidOperationException>(()=>
                db.IniciarSesion("admin","ClaveSegura2026"));
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void AuditoriaEncadenadaDetectaManipulacionesDelHistorial()
    {
        var (db,dir)=Preparar();
        try
        {
            db.CrearAdministradorInicial("admin","ClaveSegura2026");
            db.IniciarSesion("admin","ClaveSegura2026");
            db.RegistrarIngreso("AA111BB","Auto","General");
            db.CambiarTarifa("Moto",100000,50000,30,600000,10);
            Assert.True(db.VerificarCadenaAuditoria());
            using(var conexion=new SqliteConnection($"Data Source={db.RutaDb}"))
            {
                conexion.Open();
                using var cmd=conexion.CreateCommand();
                cmd.CommandText="""
                    UPDATE AuditoriaSeguridad SET Detalle='MODIFICADO'
                    WHERE Secuencia=(SELECT MAX(Secuencia) FROM AuditoriaSeguridad);
                    """;
                Assert.Equal(1,cmd.ExecuteNonQuery());
            }
            Assert.False(db.VerificarCadenaAuditoria());
        }
        finally { Limpiar(dir); }
    }

    [Fact]
    public void RevocarRolYResetearClaveRequierenAdministrador()
    {
        var (db,dir)=Preparar();
        try
        {
            db.CrearAdministradorInicial("admin","ClaveSegura2026");
            db.IniciarSesion("admin","ClaveSegura2026");
            var user=db.CrearUsuario("cajero01","ClaveCajero2026",RolesAparcar.Cajero);
            db.RestablecerContrasenaUsuario(user.Id,"OtraClaveCajero2026");
            db.CambiarRolUsuario(user.Id,RolesAparcar.Consulta);
            db.CerrarSesion();
            Assert.Throws<InvalidOperationException>(()=>
                db.IniciarSesion("cajero01","ClaveCajero2026"));
            var consulta=db.IniciarSesion("cajero01","OtraClaveCajero2026");
            Assert.Equal(RolesAparcar.Consulta,consulta.Rol);
            Assert.Throws<UnauthorizedAccessException>(()=>
                db.AbrirTurno("Operador",0));
            Assert.Empty(db.ListarActivas());
        }
        finally { Limpiar(dir); }
    }
}
