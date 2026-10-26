using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private const string RecuperacionNombre="recuperacion-pendiente.db";
    private const string RecuperacionMarcador="recuperacion-pendiente.sha256";

    private static SqliteConnection AbrirRespaldoLectura(string ruta)
    {
        if(!File.Exists(ruta))throw new FileNotFoundException("No existe el respaldo indicado.",ruta);
        if(new FileInfo(ruta).Length<1024)
            throw new InvalidOperationException("El archivo no contiene una base SQLite válida.");
        var cn=new SqliteConnection(new SqliteConnectionStringBuilder{
            DataSource=Path.GetFullPath(ruta),Mode=SqliteOpenMode.ReadOnly,Pooling=false
        }.ToString());
        cn.Open();
        return cn;
    }

    private static DiagnosticoRespaldo DiagnosticarRespaldo(string ruta)
    {
        var absoluta=Path.GetFullPath(ruta);
        using var origen=AbrirRespaldoLectura(absoluta);
        using(var check=origen.CreateCommand())
        {
            check.CommandText="PRAGMA quick_check;";
            if(!string.Equals(check.ExecuteScalar()?.ToString(),"ok",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite informó errores de integridad en el respaldo.");
        }
        using(var check=origen.CreateCommand())
        {
            check.CommandText="PRAGMA foreign_key_check;";
            using var filas=check.ExecuteReader();
            if(filas.Read())throw new InvalidOperationException("El respaldo contiene relaciones dañadas.");
        }
        var version=0;
        long estadias;
        using(var cmd=origen.CreateCommand())
        {
            cmd.CommandText="""
                SELECT COUNT(*) FROM sqlite_master WHERE type='table'
                  AND name IN('EsquemaVersion','Estadias');
                """;
            if(Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=2)
                throw new InvalidOperationException("El archivo no es un respaldo de Aparcar.");
        }
        using(var cmd=origen.CreateCommand())
        {
            cmd.CommandText="SELECT COALESCE(MAX(Numero),0) FROM EsquemaVersion";
            version=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
            if(version is < 1 or > 18)
                throw new InvalidOperationException("Versión de datos no compatible con este Aparcar.");
        }
        using(var cmd=origen.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM Estadias";
            estadias=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        using var contenido=File.OpenRead(absoluta);
        var hash=Convert.ToHexString(SHA256.HashData(contenido));
        return new DiagnosticoRespaldo(absoluta,new FileInfo(absoluta).Length,
            hash,version,estadias,true);
    }

    public DiagnosticoRespaldo VerificarRespaldo(string ruta)
    {
        ExigirPermiso(PermisosAparcar.Respaldo);
        return DiagnosticarRespaldo(ruta);
    }

    public DiagnosticoRespaldo PrepararRestauracion(string ruta)
    {
        ExigirPermiso(PermisosAparcar.Restauracion);
        var origen=Path.GetFullPath(ruta);
        if(string.Equals(origen,RutaDb,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Elegí un archivo de respaldo, no la base activa.");
        _=DiagnosticarRespaldo(origen);
        if(ObtenerTurnoAbierto() is not null)
            throw new InvalidOperationException("Cerrá el turno de caja antes de preparar una restauración.");
        var carpeta=Path.GetDirectoryName(RutaDb)!;
        var destino=Path.Combine(carpeta,RecuperacionNombre);
        var marcador=Path.Combine(carpeta,RecuperacionMarcador);
        if(File.Exists(destino)||File.Exists(marcador))
            throw new InvalidOperationException("Ya existe una recuperación pendiente. Reiniciá Aparcar antes de preparar otra.");
        // BackupDatabase incorpora páginas WAL si el respaldo aún tiene una conexión SQLite abierta.
        var temporal=destino+".temporal";
        try
        {
            using(var fuente=AbrirRespaldoLectura(origen))
            using(var copia=new SqliteConnection(new SqliteConnectionStringBuilder{
                DataSource=temporal,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false
            }.ToString()))
            {
                copia.Open();
                fuente.BackupDatabase(copia);
            }
            var resultado=DiagnosticarRespaldo(temporal);
            File.Move(temporal,destino);
            File.WriteAllText(marcador+".temporal",resultado.HashSha256);
            File.Move(marcador+".temporal",marcador);
            RegistrarAuditoria("Respaldo.PrepararRestauracion","BaseLocal",
                $"Solicitud preparada: versión {resultado.VersionEsquema}; estadías {resultado.CantidadEstadias}");
            return resultado with {Ruta=destino};
        }
        catch
        {
            if(File.Exists(temporal))File.Delete(temporal);
            if(File.Exists(marcador+".temporal"))File.Delete(marcador+".temporal");
            if(File.Exists(destino) && !File.Exists(marcador))File.Delete(destino);
            throw;
        }
    }

    /// <summary>
    /// Se invoca antes de abrir AparcarDb al iniciar la aplicación.
    /// No restaura durante un turno activo en memoria ni con conexiones abiertas.
    /// Conserva una copia de la base sustituida para recuperación de emergencia.
    /// </summary>
    public static string? AplicarRestauracionPendiente(string rutaDb)
    {
        var destino=Path.GetFullPath(rutaDb);
        var carpeta=Path.GetDirectoryName(destino)!;
        var etapa=Path.Combine(carpeta,RecuperacionNombre);
        var marcador=Path.Combine(carpeta,RecuperacionMarcador);
        if(!File.Exists(marcador))return null;
        if(!File.Exists(etapa))
            throw new InvalidOperationException("Existe una solicitud de restauración, pero falta el respaldo preparado.");
        string esperado=File.ReadAllText(marcador).Trim();
        if(esperado.Length!=64 || !string.Equals(DiagnosticarRespaldo(etapa).HashSha256,
            esperado,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El respaldo preparado cambió o está dañado. No se reemplazó la base activa.");

        Directory.CreateDirectory(carpeta);
        var resguardo=Path.Combine(carpeta,
            $"antes_restauracion_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db");
        bool habiaDatos=File.Exists(destino);
        SqliteConnection.ClearAllPools();
        if(habiaDatos)
            CopiarBaseSqlite(destino,resguardo);
        try
        {
            CopiarBaseSqlite(etapa,destino);
            _=DiagnosticarRespaldo(destino);
            File.Delete(marcador);
            File.Delete(etapa);
            return habiaDatos?resguardo:"Se recuperó una base sin datos anteriores.";
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if(habiaDatos && File.Exists(resguardo))
                CopiarBaseSqlite(resguardo,destino);
            else if(!habiaDatos && File.Exists(destino))File.Delete(destino);
            throw;
        }
    }

    private static void CopiarBaseSqlite(string origen,string destino)
    {
        using var fuente=AbrirRespaldoLectura(origen);
        using var target=new SqliteConnection(new SqliteConnectionStringBuilder{
            DataSource=destino,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false
        }.ToString());
        target.Open();
        fuente.BackupDatabase(target);
    }
}
