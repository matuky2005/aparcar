using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarV9()
    {
        using(var ver=Abrir())
        using(var cmd=ver.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=9";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var cmdMigracion=db.CreateCommand();
        cmdMigracion.Transaction=tx;
        cmdMigracion.CommandText="""
            CREATE TABLE IF NOT EXISTS HorariosTarifariosSector(
                Sector TEXT PRIMARY KEY COLLATE NOCASE REFERENCES Sectores(Nombre),
                InicioNoche INTEGER NOT NULL CHECK(InicioNoche BETWEEN 0 AND 1439),
                FinNoche INTEGER NOT NULL CHECK(FinNoche BETWEEN 0 AND 1439),
                CHECK(InicioNoche!=FinNoche)
            );
            CREATE TABLE IF NOT EXISTS TarifasSectorVersionadas(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Sector TEXT NOT NULL COLLATE NOCASE REFERENCES Sectores(Nombre),
                TipoVehiculo TEXT NOT NULL,
                Franja TEXT NOT NULL CHECK(Franja IN('Diurna','Nocturna')),
                Version INTEGER NOT NULL CHECK(Version>0),
                PrimeraHoraCentavos INTEGER NOT NULL CHECK(PrimeraHoraCentavos>=0),
                FraccionCentavos INTEGER NOT NULL CHECK(FraccionCentavos>=0),
                MinutosFraccion INTEGER NOT NULL CHECK(MinutosFraccion BETWEEN 1 AND 1440),
                Tope24HorasCentavos INTEGER NOT NULL CHECK(Tope24HorasCentavos>=PrimeraHoraCentavos),
                CortesiaMinutos INTEGER NOT NULL CHECK(CortesiaMinutos BETWEEN 0 AND 60),
                VigenteDesdeUtc TEXT NOT NULL,
                UNIQUE(Sector,TipoVehiculo,Franja,Version)
            );
            CREATE TABLE IF NOT EXISTS EstadiaTarifaSector(
                EstadiaId TEXT PRIMARY KEY REFERENCES Estadias(Id),
                TarifaSectorVersionId INTEGER NOT NULL REFERENCES TarifasSectorVersionadas(Id),
                FranjaAplicada TEXT NOT NULL CHECK(FranjaAplicada IN('Diurna','Nocturna'))
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(9,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmdMigracion.ExecuteNonQuery();
        tx.Commit();
    }

    public HorarioTarifarioSector ObtenerHorarioTarifario(string sector)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        var canonico=ObtenerSectorCanonico(db,null,sector);
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT InicioNoche,FinNoche FROM HorariosTarifariosSector
            WHERE Sector=$sector;
            """;
        cmd.Parameters.AddWithValue("$sector",canonico);
        using var reader=cmd.ExecuteReader();
        return reader.Read()
            ?new HorarioTarifarioSector(canonico,TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(reader.GetInt32(0))),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(reader.GetInt32(1))))
            :new HorarioTarifarioSector(canonico,new TimeOnly(20,0),new TimeOnly(8,0));
    }

    private static string ObtenerSectorCanonico(SqliteConnection db,SqliteTransaction? tx,string sector)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="SELECT Nombre FROM Sectores WHERE Nombre=$sector COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$sector",SectorValido(sector));
        return cmd.ExecuteScalar() as string ??
            throw new InvalidOperationException("El sector solicitado no existe.");
    }

    public HorarioTarifarioSector ConfigurarHorarioTarifario(string sector,
        TimeOnly inicioNoche,TimeOnly finNoche)
    {
        ExigirPermiso(PermisosAparcar.Tarifas);
        if(inicioNoche==finNoche)
            throw new ArgumentException("El inicio y el fin no pueden coincidir.");
        if(inicioNoche.Second!=0||finNoche.Second!=0)
            throw new ArgumentException("Los horarios deben configurarse por minuto.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        var nombre=ObtenerSectorCanonico(db,tx,sector);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO HorariosTarifariosSector(Sector,InicioNoche,FinNoche)
                VALUES($sector,$desde,$hasta)
                ON CONFLICT(Sector) DO UPDATE SET
                    InicioNoche=excluded.InicioNoche,FinNoche=excluded.FinNoche;
                """;
            cmd.Parameters.AddWithValue("$sector",nombre);
            cmd.Parameters.AddWithValue("$desde",inicioNoche.Hour*60+inicioNoche.Minute);
            cmd.Parameters.AddWithValue("$hasta",finNoche.Hour*60+finNoche.Minute);
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Tarifa.Horario",nombre,$"Nocturna {inicioNoche:HH:mm}-{finNoche:HH:mm}");
        tx.Commit();
        return new HorarioTarifarioSector(nombre,inicioNoche,finNoche);
    }

    public TarifaSectorVersionada GuardarTarifaSector(string sector,string tipo,string franja,
        long primera,long fraccion,int minutosFraccion,long tope,int cortesia)
    {
        ExigirPermiso(PermisosAparcar.Tarifas);
        if(!TipoHabilitado(tipo)||!FranjasTarifarias.Valida(franja))
            throw new ArgumentException("Tipo o franja inválidos.");
        MotorTarifas.Validar(new TarifaOperativa(0,tipo,0,primera,fraccion,
            minutosFraccion,tope,cortesia));
        using var db=Abrir();using var tx=db.BeginTransaction();
        var nombre=ObtenerSectorCanonico(db,tx,sector);
        int nuevaVersion;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COALESCE(MAX(Version),0)+1 FROM TarifasSectorVersionadas
                WHERE Sector=$sector AND TipoVehiculo=$tipo AND Franja=$franja;
                """;
            cmd.Parameters.AddWithValue("$sector",nombre);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$franja",franja);
            nuevaVersion=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        long id;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO TarifasSectorVersionadas(
                    Sector,TipoVehiculo,Franja,Version,PrimeraHoraCentavos,
                    FraccionCentavos,MinutosFraccion,Tope24HorasCentavos,CortesiaMinutos,VigenteDesdeUtc)
                VALUES($sector,$tipo,$franja,$version,$primera,$fraccion,$minutos,$tope,$cortesia,$fecha);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$sector",nombre);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$franja",franja);
            cmd.Parameters.AddWithValue("$version",nuevaVersion);
            cmd.Parameters.AddWithValue("$primera",primera);
            cmd.Parameters.AddWithValue("$fraccion",fraccion);
            cmd.Parameters.AddWithValue("$minutos",minutosFraccion);
            cmd.Parameters.AddWithValue("$tope",tope);
            cmd.Parameters.AddWithValue("$cortesia",cortesia);
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            id=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        AuditarSesion(db,tx,"Tarifa.Sector",id.ToString(CultureInfo.InvariantCulture),
            $"{nombre}/{tipo}/{franja} v{nuevaVersion}; primera={primera}; fraccion={fraccion}; tope={tope}");
        tx.Commit();
        return new TarifaSectorVersionada(id,nombre,tipo,franja,nuevaVersion,
            primera,fraccion,minutosFraccion,tope,cortesia);
    }

    public TarifaSectorVersionada? ObtenerTarifaSector(string sector,string tipo,string franja)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        var canonico=ObtenerSectorCanonico(db,null,sector);
        return TarifaSectorActual(db,null,canonico,tipo,franja);
    }

    private static TarifaSectorVersionada? TarifaSectorActual(SqliteConnection db,
        SqliteTransaction? tx,string sector,string tipo,string franja)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT Id,Sector,TipoVehiculo,Franja,Version,PrimeraHoraCentavos,
                   FraccionCentavos,MinutosFraccion,Tope24HorasCentavos,CortesiaMinutos
            FROM TarifasSectorVersionadas
            WHERE Sector=$sector AND TipoVehiculo=$tipo AND Franja=$franja
            ORDER BY Version DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$sector",sector);
        cmd.Parameters.AddWithValue("$tipo",tipo);
        cmd.Parameters.AddWithValue("$franja",franja);
        using var r=cmd.ExecuteReader();
        return r.Read()?new TarifaSectorVersionada(r.GetInt64(0),r.GetString(1),r.GetString(2),
            r.GetString(3),r.GetInt32(4),r.GetInt64(5),r.GetInt64(6),
            r.GetInt32(7),r.GetInt64(8),r.GetInt32(9)):null;
    }

    private static void AsignarTarifaSectorAlIngresar(SqliteConnection db,SqliteTransaction tx,
        string estadiaId,string sector,string tipo,DateTimeOffset ingresoUtc)
    {
        int inicio=20*60,fin=8*60;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT InicioNoche,FinNoche FROM HorariosTarifariosSector WHERE Sector=$sector;
                """;
            cmd.Parameters.AddWithValue("$sector",sector);
            using var r=cmd.ExecuteReader();
            if(r.Read()){inicio=r.GetInt32(0);fin=r.GetInt32(1);}
        }
        var hora=TimeOnly.FromDateTime(ingresoUtc.ToLocalTime().DateTime);
        var franja=FranjasTarifarias.Elegir(hora,
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(inicio)),
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(fin)));
        var vigente=TarifaSectorActual(db,tx,sector,tipo,franja);
        if(vigente is null)return; // Continúa vigente la tarifa general congelada al ingreso.
        using var cmdInsert=db.CreateCommand();
        cmdInsert.Transaction=tx;
        cmdInsert.CommandText="""
            INSERT INTO EstadiaTarifaSector(EstadiaId,TarifaSectorVersionId,FranjaAplicada)
            VALUES($estadia,$tarifa,$franja);
            """;
        cmdInsert.Parameters.AddWithValue("$estadia",estadiaId);
        cmdInsert.Parameters.AddWithValue("$tarifa",vigente.Id);
        cmdInsert.Parameters.AddWithValue("$franja",franja);
        cmdInsert.ExecuteNonQuery();
    }

    private static TarifaOperativa? TarifaSectorDeEstadia(
        SqliteConnection db,SqliteTransaction? tx,Guid estadiaId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT t.Id,t.TipoVehiculo,t.Version,t.PrimeraHoraCentavos,
                t.FraccionCentavos,t.MinutosFraccion,t.Tope24HorasCentavos,t.CortesiaMinutos
            FROM EstadiaTarifaSector e
            JOIN TarifasSectorVersionadas t ON t.Id=e.TarifaSectorVersionId
            WHERE e.EstadiaId=$estadia;
            """;
        cmd.Parameters.AddWithValue("$estadia",estadiaId.ToString("D"));
        using var r=cmd.ExecuteReader();
        return r.Read()?LeerTarifa(r):null;
    }
}
