using System;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Aparcar.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aparcar.Tests;

public sealed class RespaldoV9Tests
{
    private static (AparcarDb Db,string Dir) Preparar()
    {
        var dir=Path.Combine(Path.GetTempPath(),"AparcarRecuperacionV9",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=new AparcarDb(Path.Combine(dir,"aparcar.db"));
        db.Inicializar();
        db.CrearAdministradorInicial("AdminBackup","ClaveSegura2026");
        db.IniciarSesion("AdminBackup","ClaveSegura2026");
        return(db,dir);
    }

    private static void Quitar(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir,true);
    }

    private static string CopiaSqlite(string original,string destino)
    {
        using var origen=new SqliteConnection($"Data Source={original};Mode=ReadOnly;Pooling=False");
        using var copia=new SqliteConnection($"Data Source={destino};Pooling=False");
        origen.Open();
        copia.Open();
        origen.BackupDatabase(copia);
        return destino;
    }

    [Fact]
    public void ComprobarRespaldoRechazaArchivosQueNoSeanSQLiteAparcar()
    {
        var (db,dir)=Preparar();
        try
        {
            var basura=Path.Combine(dir,"archivo.db");
            File.WriteAllText(basura,new string('x',3000));
            Assert.ThrowsAny<Exception>(()=>db.VerificarRespaldo(basura));
            var valido=CopiaSqlite(db.RutaDb,Path.Combine(dir,"valido.db"));
            var info=db.VerificarRespaldo(valido);
            Assert.True(info.IntegridadCorrecta);
            Assert.Equal(10,info.VersionEsquema);
            Assert.Equal(64,info.HashSha256.Length);
        }
        finally{Quitar(dir);}
    }

    [Fact]
    public void RestauracionSeAplicaSoloAlReiniciar_YConservaLaBaseReemplazada()
    {
        var (db,dir)=Preparar();
        try
        {
            db.RegistrarIngreso("AA111AA","Auto","General");
            var anterior=CopiaSqlite(db.RutaDb,Path.Combine(dir,"backup_previo.db"));
            db.RegistrarIngreso("BB222BB","Moto","General");
            Assert.Equal(2,db.ListarActivas().Count);
            var listo=db.PrepararRestauracion(anterior);
            Assert.Equal(10,listo.VersionEsquema);
            Assert.Equal(2,db.ListarActivas().Count); // Aún NO se cambió la base en uso
            Assert.Throws<InvalidOperationException>(()=>db.PrepararRestauracion(anterior));
            db.CerrarSesion();
            SqliteConnection.ClearAllPools();

            var resguardo=AparcarDb.AplicarRestauracionPendiente(db.RutaDb);
            Assert.False(string.IsNullOrWhiteSpace(resguardo));
            Assert.True(File.Exists(resguardo));
            var nueva=new AparcarDb(db.RutaDb);
            nueva.Inicializar();
            nueva.IniciarSesion("AdminBackup","ClaveSegura2026");
            Assert.Equal("AA111AA",Assert.Single(nueva.ListarActivas()).Patente);
            Assert.Null(AparcarDb.AplicarRestauracionPendiente(db.RutaDb));
            var original=new AparcarDb(resguardo!);
            original.Inicializar();
            original.IniciarSesion("AdminBackup","ClaveSegura2026");
            Assert.Equal(2,original.ListarActivas().Count);
        }
        finally{Quitar(dir);}
    }

    [Fact]
    public void RespaldoAlteradoNoSobrescribeBaseActual()
    {
        var (db,dir)=Preparar();
        try
        {
            db.RegistrarIngreso("AC222AB","Auto","General");
            var copia=CopiaSqlite(db.RutaDb,Path.Combine(dir,"fuente.db"));
            db.PrepararRestauracion(copia);
            db.RegistrarIngreso("AD333AB","Auto","General");
            db.CerrarSesion();
            SqliteConnection.ClearAllPools();
            var preparado=Path.Combine(dir,"recuperacion-pendiente.db");
            File.AppendAllText(preparado,"cambio");
            Assert.ThrowsAny<Exception>(()=>AparcarDb.AplicarRestauracionPendiente(db.RutaDb));
            var intacta=new AparcarDb(db.RutaDb);
            intacta.Inicializar();
            intacta.IniciarSesion("AdminBackup","ClaveSegura2026");
            Assert.Equal(2,intacta.ListarActivas().Count);
        }
        finally{Quitar(dir);}
    }

    [Fact]
    public void CajeroNoPuedePrepararRestauracion()
    {
        var (db,dir)=Preparar();
        try
        {
            var copia=CopiaSqlite(db.RutaDb,Path.Combine(dir,"fuente.db"));
            db.CrearUsuario("CajeroBk","ClaveCajero2026",RolesAparcar.Cajero);
            db.CerrarSesion();
            db.IniciarSesion("CajeroBk","ClaveCajero2026");
            Assert.Throws<UnauthorizedAccessException>(()=>db.PrepararRestauracion(copia));
        }
        finally{Quitar(dir);}
    }
}
