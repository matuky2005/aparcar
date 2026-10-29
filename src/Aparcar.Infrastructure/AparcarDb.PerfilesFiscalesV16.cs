using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed record PerfilFiscalEmisorVersion(
    string Cuit,int Version,EmisorDocumentoFiscal Emisor,
    DateTimeOffset CreadoUtc,bool EsVigente);

/// <summary>
/// v0.16: perfiles por CUIT, versionados en SQLite. Cada versión es inmutable,
/// con pointer a la última versión en transacción. No constituye consulta
/// ni validación tributaria en padrones ARCA.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV16Perfiles()
    {
        using(var consulta=Abrir())
        using(var cmd=consulta.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=14";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmdCrear=db.CreateCommand();
        cmdCrear.Transaction=tx;
        cmdCrear.CommandText="""
            CREATE TABLE IF NOT EXISTS VersionesPerfilesFiscales(
                CuitEmisor TEXT NOT NULL,
                Version INTEGER NOT NULL CHECK(Version >= 1),
                DatosJson TEXT NOT NULL,
                HuellaSha256 TEXT NOT NULL,
                CreadoUtc TEXT NOT NULL,
                PRIMARY KEY(CuitEmisor,Version)
            );
            CREATE TABLE IF NOT EXISTS PerfilesFiscalesActuales(
                CuitEmisor TEXT PRIMARY KEY NOT NULL,
                VersionActual INTEGER NOT NULL CHECK(VersionActual >= 1),
                FOREIGN KEY(CuitEmisor,VersionActual)
                    REFERENCES VersionesPerfilesFiscales(CuitEmisor,Version)
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(14,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmdCrear.ExecuteNonQuery();
        tx.Commit();
    }

    private static EmisorDocumentoFiscal LeerEmisorVersion(
        string cuit,string json,string hash)
    {
        if(!string.Equals(Huella(json),hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Perfil fiscal modificado fuera de Aparcar. Se bloqueó su utilización.");
        var emisor=JsonSerializer.Deserialize<EmisorDocumentoFiscal>(json) ??
            throw new InvalidOperationException("No se pudo recuperar el perfil fiscal.");
        if(!string.Equals(emisor.Cuit,cuit,StringComparison.Ordinal) ||
           !AdaptadorWsfeFacturador.CuitValido(emisor.Cuit))
            throw new InvalidOperationException("El CUIT del perfil no coincide con su identificador.");
        return emisor;
    }

    private static PerfilFiscalEmisorVersion LeerPerfilVersion(SqliteDataReader r)
    {
        var cuit=r.GetString(0);
        var emisor=LeerEmisorVersion(cuit,r.GetString(2),r.GetString(3));
        return new PerfilFiscalEmisorVersion(
            cuit,r.GetInt32(1),emisor,
            DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),r.GetInt32(5)==1);
    }

    public PerfilFiscalEmisorVersion GuardarPerfilFiscalEmisor(EmisorDocumentoFiscal emisor)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        ValidadorDocumentoFiscalPrueba.ValidarEmisor(emisor);
        var json=JsonSerializer.Serialize(emisor);
        using var db=Abrir();
        using var tx=db.BeginTransaction();

        int actual=0;
        string? jsonActual=null;
        using(var consulta=db.CreateCommand())
        {
            consulta.Transaction=tx;
            consulta.CommandText="""
                SELECT v.Version,v.DatosJson,v.HuellaSha256
                FROM PerfilesFiscalesActuales p
                INNER JOIN VersionesPerfilesFiscales v
                  ON p.CuitEmisor=v.CuitEmisor AND p.VersionActual=v.Version
                WHERE p.CuitEmisor=$cuit;
                """;
            consulta.Parameters.AddWithValue("$cuit",emisor.Cuit);
            using var r=consulta.ExecuteReader();
            if(r.Read())
            {
                actual=r.GetInt32(0);
                jsonActual=r.GetString(1);
                LeerEmisorVersion(emisor.Cuit,jsonActual,r.GetString(2));
            }
        }
        if(jsonActual==json)
        {
            tx.Commit();
            return ObtenerPerfilFiscalEmisor(emisor.Cuit) ??
                throw new InvalidOperationException("No se recuperó la versión vigente.");
        }

        int nuevaVersion=checked(actual+1);
        var fecha=DateTimeOffset.UtcNow.ToString("O");
        using(var insertar=db.CreateCommand())
        {
            insertar.Transaction=tx;
            insertar.CommandText="""
                INSERT INTO VersionesPerfilesFiscales(
                    CuitEmisor,Version,DatosJson,HuellaSha256,CreadoUtc)
                VALUES($cuit,$version,$json,$hash,$fecha);
                """;
            insertar.Parameters.AddWithValue("$cuit",emisor.Cuit);
            insertar.Parameters.AddWithValue("$version",nuevaVersion);
            insertar.Parameters.AddWithValue("$json",json);
            insertar.Parameters.AddWithValue("$hash",Huella(json));
            insertar.Parameters.AddWithValue("$fecha",fecha);
            insertar.ExecuteNonQuery();
        }
        using(var actualizar=db.CreateCommand())
        {
            actualizar.Transaction=tx;
            actualizar.CommandText="""
                INSERT INTO PerfilesFiscalesActuales(CuitEmisor,VersionActual)
                VALUES($cuit,$version)
                ON CONFLICT(CuitEmisor) DO UPDATE SET VersionActual=excluded.VersionActual;
                """;
            actualizar.Parameters.AddWithValue("$cuit",emisor.Cuit);
            actualizar.Parameters.AddWithValue("$version",nuevaVersion);
            actualizar.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Fiscal.PerfilEmisor",emisor.Cuit,
            $"Perfil fiscal versionado: v{nuevaVersion}; hash={Huella(json)}");
        tx.Commit();
        return new PerfilFiscalEmisorVersion(
            emisor.Cuit,nuevaVersion,emisor,
            DateTimeOffset.Parse(fecha,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            true);
    }

    public PerfilFiscalEmisorVersion? ObtenerPerfilFiscalEmisor(
        string cuit,int? version=null)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(!AdaptadorWsfeFacturador.CuitValido(cuit))
            throw new ArgumentException("CUIT de consulta inválido.");
        if(version is <1)throw new ArgumentOutOfRangeException(nameof(version));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT v.CuitEmisor,v.Version,v.DatosJson,v.HuellaSha256,v.CreadoUtc,
                CASE WHEN v.Version=p.VersionActual THEN 1 ELSE 0 END
            FROM VersionesPerfilesFiscales v
            INNER JOIN PerfilesFiscalesActuales p ON v.CuitEmisor=p.CuitEmisor
            WHERE v.CuitEmisor=$cuit AND v.Version=COALESCE($version,p.VersionActual);
            """;
        cmd.Parameters.AddWithValue("$cuit",cuit);
        cmd.Parameters.AddWithValue("$version",version is null?DBNull.Value:(object)version.Value);
        using var r=cmd.ExecuteReader();
        return r.Read()?LeerPerfilVersion(r):null;
    }

    public IReadOnlyList<PerfilFiscalEmisorVersion> ListarPerfilesFiscales(int max=200)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT v.CuitEmisor,v.Version,v.DatosJson,v.HuellaSha256,v.CreadoUtc,1
            FROM PerfilesFiscalesActuales p
            INNER JOIN VersionesPerfilesFiscales v
                ON p.CuitEmisor=v.CuitEmisor AND p.VersionActual=v.Version
            ORDER BY v.CuitEmisor LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<PerfilFiscalEmisorVersion>();
        while(r.Read())lista.Add(LeerPerfilVersion(r));
        return lista;
    }

    public IReadOnlyList<PerfilFiscalEmisorVersion> ListarVersionesPerfilFiscal(
        string cuit,int max=100)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(!AdaptadorWsfeFacturador.CuitValido(cuit) || max is <1 or >1000)
            throw new ArgumentException("CUIT o límite de consulta inválido.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT v.CuitEmisor,v.Version,v.DatosJson,v.HuellaSha256,v.CreadoUtc,
                CASE WHEN v.Version=p.VersionActual THEN 1 ELSE 0 END
            FROM VersionesPerfilesFiscales v
            INNER JOIN PerfilesFiscalesActuales p ON v.CuitEmisor=p.CuitEmisor
            WHERE v.CuitEmisor=$cuit
            ORDER BY v.Version DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$cuit",cuit);
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<PerfilFiscalEmisorVersion>();
        while(r.Read())lista.Add(LeerPerfilVersion(r));
        return lista;
    }

    /// <summary>
    /// Llamar DENTRO de la transacción del snapshot fiscal antes de INSERT.
    /// No confía en la versión declarada desde UI; compara datos y hash almacenados.
    /// </summary>
    private static void ValidarReferenciaPerfilFiscal(
        SqliteConnection db,SqliteTransaction tx,DatosDocumentoFiscalPrueba datos)
    {
        if(datos.VersionPerfilEmisor is not int version)return;
        if(version<1)throw new ArgumentException("Versión de perfil inválida.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT DatosJson,HuellaSha256 FROM VersionesPerfilesFiscales
            WHERE CuitEmisor=$cuit AND Version=$version;
            """;
        cmd.Parameters.AddWithValue("$cuit",datos.Emisor.Cuit);
        cmd.Parameters.AddWithValue("$version",version);
        using var r=cmd.ExecuteReader();
        if(!r.Read())
            throw new InvalidOperationException("La versión fiscal seleccionada no existe.");
        var json=r.GetString(0);
        LeerEmisorVersion(datos.Emisor.Cuit,json,r.GetString(1));
        if(!string.Equals(json,JsonSerializer.Serialize(datos.Emisor),StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Los datos del emisor fueron modificados después de aplicar el perfil versionado.");
    }
}
