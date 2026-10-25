using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarV10()
    {
        using(var consulta=Abrir())
        using(var cmd=consulta.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=10";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var migrar=db.CreateCommand();
        migrar.Transaction=tx;
        migrar.CommandText="""
            CREATE TABLE IF NOT EXISTS TurnoControles(
                TurnoId TEXT PRIMARY KEY REFERENCES TurnosCaja(Id),
                CajaCiega INTEGER NOT NULL CHECK(CajaCiega IN(0,1)),
                ConfiguradoPor TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AutorizacionesReimpresion(
                Id TEXT PRIMARY KEY,
                EstadiaId TEXT NOT NULL REFERENCES Estadias(Id),
                Ticket INTEGER NOT NULL,
                AutorizadoPor TEXT NOT NULL,
                Motivo TEXT NOT NULL,
                FechaUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Reimpresion_Ticket
                ON AutorizacionesReimpresion(Ticket,FechaUtc);
            CREATE TABLE IF NOT EXISTS PendientesFiscales(
                Id TEXT PRIMARY KEY,
                Origen TEXT NOT NULL CHECK(Origen IN('Estacionamiento','Lavadero','Abono')),
                CobroId TEXT NOT NULL,
                ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos>0),
                FechaUtc TEXT NOT NULL,
                Estado TEXT NOT NULL CHECK(Estado IN('PendienteDatosFiscales','RevisionHistorica','RevisionDevolucion')),
                UNIQUE(Origen,CobroId)
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(10,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        migrar.ExecuteNonQuery();
        tx.Commit();
    }

    private static void RegistrarPendienteFiscal(SqliteConnection db,SqliteTransaction tx,
        string origen,Guid cobroId,long importeCentavos,DateTimeOffset fechaUtc)
    {
        if(importeCentavos<=0)return;
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO PendientesFiscales(Id,Origen,CobroId,ImporteCentavos,FechaUtc,Estado)
            VALUES($id,$origen,$cobro,$importe,$fecha,'PendienteDatosFiscales');
            """;
        cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$origen",origen);
        cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
        cmd.Parameters.AddWithValue("$importe",importeCentavos);
        cmd.Parameters.AddWithValue("$fecha",fechaUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void MarcarPendienteFiscalDevuelto(SqliteConnection db,SqliteTransaction tx,
        string origen,Guid cobroId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE PendientesFiscales SET Estado='RevisionDevolucion'
            WHERE Origen=$origen AND CobroId=$cobro AND Estado='PendienteDatosFiscales';
            """;
        cmd.Parameters.AddWithValue("$origen",origen);
        cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PendienteFiscal> ListarPendientesFiscales(int max=150)
    {
        ExigirPermiso(PermisosAparcar.Reportes);
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,Origen,CobroId,ImporteCentavos,FechaUtc,Estado
            FROM PendientesFiscales
            ORDER BY FechaUtc DESC,Id DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        var lista=new List<PendienteFiscal>();
        using var r=cmd.ExecuteReader();
        while(r.Read())
            lista.Add(new PendienteFiscal(Guid.Parse(r.GetString(0)),r.GetString(1),
                Guid.Parse(r.GetString(2)),r.GetInt64(3),
                DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),r.GetString(5)));
        return lista;
    }

    public bool CajaCiegaObligatoria()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Valor FROM PreferenciasDispositivos WHERE Clave='CajaCiegaObligatoria'";
        return string.Equals(cmd.ExecuteScalar() as string,"true",StringComparison.OrdinalIgnoreCase);
    }

    public void ConfigurarCajaCiegaObligatoria(bool obligatoria)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();using var tx=db.BeginTransaction();
        if(LeerTurno(db,tx) is not null)
            throw new InvalidOperationException("Cerrá el turno actual antes de cambiar la política de caja.");
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO PreferenciasDispositivos(Clave,Valor)
                VALUES('CajaCiegaObligatoria',$valor)
                ON CONFLICT(Clave) DO UPDATE SET Valor=excluded.Valor;
                """;
            cmd.Parameters.AddWithValue("$valor",obligatoria?"true":"false");
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Caja.PoliticaCiega","Estacionamiento",
            obligatoria?"Obligatoria":"Opcional");
        tx.Commit();
    }

    public bool EsTurnoCiego(Guid turnoId)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        return ConsultarTurnoCiego(db,null,turnoId);
    }

    private static bool ConsultarTurnoCiego(SqliteConnection db,SqliteTransaction? tx,Guid turnoId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="SELECT CajaCiega FROM TurnoControles WHERE TurnoId=$id";
        cmd.Parameters.AddWithValue("$id",turnoId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar()??0,CultureInfo.InvariantCulture)==1;
    }

    private bool OcultarImportesTurno(SqliteConnection db,Guid turnoId) =>
        ConsultarTurnoCiego(db,null,turnoId) &&
        SesionActual is {Rol: not (RolesAparcar.Administrador or RolesAparcar.Supervisor)};

    public AutorizacionReimpresion AutorizarReimpresion(long ticket,string motivo)
    {
        ExigirPermiso(PermisosAparcar.Reimpresion);
        if(ticket<=0)throw new ArgumentOutOfRangeException(nameof(ticket));
        if(string.IsNullOrWhiteSpace(motivo)||motivo.Trim().Length is < 8 or > 250)
            throw new ArgumentException("La reimpresión exige motivo de 8 a 250 caracteres.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        Guid estadiaId;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Id FROM Estadias WHERE Ticket=$ticket";
            cmd.Parameters.AddWithValue("$ticket",ticket);
            var id=cmd.ExecuteScalar() as string ??
                throw new InvalidOperationException("No existe el ticket solicitado.");
            estadiaId=Guid.Parse(id);
        }
        var ahora=DateTimeOffset.UtcNow;
        var permiso=new AutorizacionReimpresion(Guid.NewGuid(),estadiaId,ticket,
            SesionActual?.Usuario??"Sistema",motivo.Trim(),ahora);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO AutorizacionesReimpresion(
                    Id,EstadiaId,Ticket,AutorizadoPor,Motivo,FechaUtc)
                VALUES($id,$estadia,$ticket,$actor,$motivo,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",permiso.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$estadia",estadiaId.ToString("D"));
            cmd.Parameters.AddWithValue("$ticket",ticket);
            cmd.Parameters.AddWithValue("$actor",permiso.Autoriza);
            cmd.Parameters.AddWithValue("$motivo",permiso.Motivo);
            cmd.Parameters.AddWithValue("$fecha",ahora.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Ticket.ReimpresionAutorizada",estadiaId.ToString("D"),
            $"Ticket={ticket}; motivo={permiso.Motivo}");
        tx.Commit();
        return permiso;
    }

    public IReadOnlyList<AutorizacionReimpresion> ListarAutorizacionesReimpresion(int max=100)
    {
        ExigirPermiso(PermisosAparcar.Auditoria);
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,EstadiaId,Ticket,AutorizadoPor,Motivo,FechaUtc
            FROM AutorizacionesReimpresion ORDER BY FechaUtc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        var lista=new List<AutorizacionReimpresion>();
        using var r=cmd.ExecuteReader();
        while(r.Read())lista.Add(new AutorizacionReimpresion(
            Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetInt64(2),
            r.GetString(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(5),
                CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)));
        return lista;
    }
}
