using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarV8()
    {
        using(var ver=Abrir())
        using(var verificar=ver.CreateCommand())
        {
            verificar.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=8";
            if(Convert.ToInt64(verificar.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS Sectores(
                Nombre TEXT PRIMARY KEY COLLATE NOCASE,
                Capacidad INTEGER NOT NULL CHECK(Capacidad BETWEEN 1 AND 100000),
                Modo TEXT NOT NULL CHECK(Modo IN('Libre','Asignado')),
                Activo INTEGER NOT NULL CHECK(Activo IN(0,1))
            );
            CREATE TABLE IF NOT EXISTS Plazas(
                Sector TEXT NOT NULL REFERENCES Sectores(Nombre),
                Codigo TEXT NOT NULL COLLATE NOCASE,
                Habilitada INTEGER NOT NULL CHECK(Habilitada IN(0,1)),
                PRIMARY KEY(Sector,Codigo)
            );
            CREATE TABLE IF NOT EXISTS OcupacionesPlaza(
                EstadiaId TEXT PRIMARY KEY REFERENCES Estadias(Id),
                Sector TEXT NOT NULL,
                Codigo TEXT NOT NULL,
                AsignadaUtc TEXT NOT NULL,
                LiberadaUtc TEXT,
                FOREIGN KEY(Sector,Codigo) REFERENCES Plazas(Sector,Codigo)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_OcupacionesPlaza_Activas
                ON OcupacionesPlaza(Sector,Codigo) WHERE LiberadaUtc IS NULL;
            INSERT OR IGNORE INTO Sectores(Nombre,Capacidad,Modo,Activo)
                VALUES('General',1000,'Libre',1);
            INSERT OR IGNORE INTO Sectores(Nombre,Capacidad,Modo,Activo)
                SELECT DISTINCT Sector,1000,'Libre',1
                FROM Estadias WHERE trim(Sector)!='';
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(8,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string SectorValido(string nombre)
    {
        if(string.IsNullOrWhiteSpace(nombre)||nombre.Trim().Length>80)
            throw new ArgumentException("El nombre del sector es obligatorio (máximo 80 caracteres).");
        return nombre.Trim();
    }

    private static string CodigoPlazaValido(string codigo)
    {
        if(string.IsNullOrWhiteSpace(codigo)||codigo.Trim().Length>25)
            throw new ArgumentException("Código de plaza obligatorio (máximo 25 caracteres).");
        return codigo.Trim().ToUpperInvariant();
    }

    public void GuardarSector(string nombre,int capacidad,string modo,bool activo=true)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        var sector=SectorValido(nombre);
        if(capacidad is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(capacidad));
        if(!ModosSector.EsValido(modo))throw new ArgumentException("Modo inválido.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        string? canonico=null,modoAnterior="";
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Nombre,Modo FROM Sectores WHERE Nombre=$nombre COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$nombre",sector);
            using var r=cmd.ExecuteReader();
            if(r.Read()){canonico=r.GetString(0);modoAnterior=r.GetString(1);}
        }
        int ocupados=0;
        if(canonico is not null)
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM Estadias WHERE Sector=$sector AND Estado='Activa'";
            cmd.Parameters.AddWithValue("$sector",canonico);
            ocupados=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
            if((!activo || modo!=modoAnterior) && ocupados>0)
                throw new InvalidOperationException("No podés desactivar ni cambiar de modo un sector ocupado.");
            if(capacidad<ocupados)
                throw new InvalidOperationException("La capacidad no puede ser menor que la ocupación actual.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO Sectores(Nombre,Capacidad,Modo,Activo)
                VALUES($nombre,$capacidad,$modo,$activo)
                ON CONFLICT(Nombre) DO UPDATE SET Capacidad=excluded.Capacidad,
                    Modo=excluded.Modo,Activo=excluded.Activo;
                """;
            cmd.Parameters.AddWithValue("$nombre",canonico??sector);
            cmd.Parameters.AddWithValue("$capacidad",capacidad);
            cmd.Parameters.AddWithValue("$modo",modo);
            cmd.Parameters.AddWithValue("$activo",activo?1:0);
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Sector.Configurar",canonico??sector,
            $"Capacidad={capacidad}; modo={modo}; activo={activo}");
        tx.Commit();
    }

    public List<SectorEstacionamiento> ListarSectores(bool soloActivos=false)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT s.Nombre,s.Capacidad,s.Modo,s.Activo,
                (SELECT COUNT(*) FROM Estadias e
                 WHERE e.Sector=s.Nombre AND e.Estado='Activa') AS Ocupados,
                (SELECT COUNT(*) FROM Plazas p
                 WHERE p.Sector=s.Nombre AND p.Habilitada=1
                 AND NOT EXISTS(
                    SELECT 1 FROM OcupacionesPlaza o
                    WHERE o.Sector=p.Sector AND o.Codigo=p.Codigo AND o.LiberadaUtc IS NULL
                 )) AS PlazasDisponibles
            FROM Sectores s WHERE ($solo=0 OR s.Activo=1) ORDER BY s.Nombre;
            """;
        cmd.Parameters.AddWithValue("$solo",soloActivos?1:0);
        using var r=cmd.ExecuteReader();
        var lista=new List<SectorEstacionamiento>();
        while(r.Read())lista.Add(new SectorEstacionamiento(
            r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3)==1,
            r.GetInt32(4),r.GetInt32(5)));
        return lista;
    }

    public void GuardarPlaza(string sector,string codigo,bool habilitada=true)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        sector=SectorValido(sector);codigo=CodigoPlazaValido(codigo);
        using var db=Abrir();using var tx=db.BeginTransaction();
        string nombre;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Nombre FROM Sectores WHERE Nombre=$sector COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$sector",sector);
            nombre=cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("Sector inexistente.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COUNT(*) FROM OcupacionesPlaza
                WHERE Sector=$sector AND Codigo=$codigo AND LiberadaUtc IS NULL;
                """;
            cmd.Parameters.AddWithValue("$sector",nombre);
            cmd.Parameters.AddWithValue("$codigo",codigo);
            if(!habilitada && Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("No se puede inhabilitar una plaza ocupada.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO Plazas(Sector,Codigo,Habilitada) VALUES($sector,$codigo,$habilitada)
                ON CONFLICT(Sector,Codigo) DO UPDATE SET Habilitada=excluded.Habilitada;
                """;
            cmd.Parameters.AddWithValue("$sector",nombre);
            cmd.Parameters.AddWithValue("$codigo",codigo);
            cmd.Parameters.AddWithValue("$habilitada",habilitada?1:0);
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Plaza.Configurar",nombre+"/"+codigo,$"Habilitada={habilitada}");
        tx.Commit();
    }

    public List<PlazaEstacionamiento> ListarPlazas(string sector)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT p.Sector,p.Codigo,p.Habilitada,
                CASE WHEN o.EstadiaId IS NULL THEN 0 ELSE 1 END,
                e.Patente
            FROM Plazas p
            LEFT JOIN OcupacionesPlaza o ON o.Sector=p.Sector
                AND o.Codigo=p.Codigo AND o.LiberadaUtc IS NULL
            LEFT JOIN Estadias e ON e.Id=o.EstadiaId
            WHERE p.Sector=$sector COLLATE NOCASE
            ORDER BY p.Codigo;
            """;
        cmd.Parameters.AddWithValue("$sector",SectorValido(sector));
        using var r=cmd.ExecuteReader();
        var lista=new List<PlazaEstacionamiento>();
        while(r.Read())lista.Add(new PlazaEstacionamiento(
            r.GetString(0),r.GetString(1),r.GetInt32(2)==1,
            r.GetInt32(3)==1,r.IsDBNull(4)?null:r.GetString(4)));
        return lista;
    }

    private static (string Nombre,string Modo) VerificarSectorAntesIngreso(
        SqliteConnection db,SqliteTransaction tx,string nombre)
    {
        string sector,modo;int capacidad;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Nombre,Capacidad,Modo FROM Sectores
                WHERE Nombre=$nombre COLLATE NOCASE AND Activo=1;
                """;
            cmd.Parameters.AddWithValue("$nombre",nombre);
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Sector inexistente o inhabilitado. Configuralo antes de ingresar.");
            sector=r.GetString(0);capacidad=r.GetInt32(1);modo=r.GetString(2);
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM Estadias WHERE Sector=$sector AND Estado='Activa'";
            cmd.Parameters.AddWithValue("$sector",sector);
            if(Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>=capacidad)
                throw new InvalidOperationException($"Sector {sector} completo: {capacidad} plazas de capacidad.");
        }
        return (sector,modo);
    }

    private static string? ElegirPlaza(SqliteConnection db,SqliteTransaction tx,
        string sector,string modo,string? solicitada)
    {
        if(modo==ModosSector.Libre)
        {
            if(!string.IsNullOrWhiteSpace(solicitada))
                throw new InvalidOperationException("Las plazas individuales requieren un sector en modo Asignado.");
            return null;
        }
        var codigo=string.IsNullOrWhiteSpace(solicitada)?null:CodigoPlazaValido(solicitada);
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT p.Codigo FROM Plazas p
            WHERE p.Sector=$sector AND p.Habilitada=1
              AND ($codigo IS NULL OR p.Codigo=$codigo COLLATE NOCASE)
              AND NOT EXISTS(SELECT 1 FROM OcupacionesPlaza o
                WHERE o.Sector=p.Sector AND o.Codigo=p.Codigo AND o.LiberadaUtc IS NULL)
            ORDER BY p.Codigo LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sector",sector);
        cmd.Parameters.AddWithValue("$codigo",codigo is null?(object)DBNull.Value:codigo);
        return cmd.ExecuteScalar() as string ??
            throw new InvalidOperationException("No hay plaza disponible o la plaza solicitada está ocupada/inhabilitada.");
    }

    private static void OcuparPlaza(SqliteConnection db,SqliteTransaction tx,
        Guid estadia,string sector,string codigo)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO OcupacionesPlaza(EstadiaId,Sector,Codigo,AsignadaUtc)
            VALUES($estadia,$sector,$codigo,$fecha);
            """;
        cmd.Parameters.AddWithValue("$estadia",estadia.ToString("D"));
        cmd.Parameters.AddWithValue("$sector",sector);
        cmd.Parameters.AddWithValue("$codigo",codigo);
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        try{cmd.ExecuteNonQuery();}
        catch(SqliteException ex) when(ex.SqliteErrorCode==19)
        {throw new InvalidOperationException("La plaza se ocupó en otra operación. Elegí una nueva.",ex);}
    }

    private static void LiberarPlaza(SqliteConnection db,SqliteTransaction tx,Guid estadia)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE OcupacionesPlaza SET LiberadaUtc=$fecha
            WHERE EstadiaId=$estadia AND LiberadaUtc IS NULL;
            """;
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$estadia",estadia.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    public UbicacionEstadia? ConsultarUbicacion(Guid estadiaId)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT e.Id,e.Sector,o.Codigo,e.Patente
            FROM Estadias e
            LEFT JOIN OcupacionesPlaza o ON o.EstadiaId=e.Id
            WHERE e.Id=$id;
            """;
        cmd.Parameters.AddWithValue("$id",estadiaId.ToString("D"));
        using var r=cmd.ExecuteReader();
        return r.Read()?new UbicacionEstadia(Guid.Parse(r.GetString(0)),
            r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3)):null;
    }
}
