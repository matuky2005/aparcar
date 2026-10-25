using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    /// <summary>Crea tablas adicionales sin modificar ni borrar estadías preexistentes.</summary>
    private void InicializarTickets()
    {
        using var db = Abrir();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS TicketsAcceso(
                    EstadiaId TEXT PRIMARY KEY REFERENCES Estadias(Id),
                    TokenQr TEXT NOT NULL UNIQUE
                );
                CREATE TABLE IF NOT EXISTS ImpresionesTicket(
                    Id TEXT PRIMARY KEY,
                    EstadiaId TEXT NOT NULL REFERENCES Estadias(Id),
                    FechaUtc TEXT NOT NULL,
                    Impresora TEXT NOT NULL,
                    Resultado TEXT NOT NULL CHECK(Resultado IN ('Enviado','Error')),
                    Detalle TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ImpresionesTicket_Estadia
                    ON ImpresionesTicket(EstadiaId, FechaUtc);
                CREATE TABLE IF NOT EXISTS PreferenciasDispositivos(
                    Clave TEXT PRIMARY KEY,
                    Valor TEXT NOT NULL
                );
                INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                    VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        }

        var pendientes = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT e.Id FROM Estadias e
                LEFT JOIN TicketsAcceso t ON t.EstadiaId=e.Id
                WHERE t.EstadiaId IS NULL;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) pendientes.Add(reader.GetString(0));
        }

        foreach (var id in pendientes)
            GuardarToken(db, tx, id);

        tx.Commit();
    }

    private static string NuevoToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static void GuardarToken(SqliteConnection db, SqliteTransaction tx, string estadiaId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO TicketsAcceso(EstadiaId,TokenQr) VALUES($id,$token)";
        cmd.Parameters.AddWithValue("$id", estadiaId);
        cmd.Parameters.AddWithValue("$token", NuevoToken());
        cmd.ExecuteNonQuery();
    }

    public TicketIngreso ObtenerTicket(long numero)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.Ticket,e.Id,e.Patente,e.TipoVehiculo,e.Sector,e.IngresoUtc,t.TokenQr,o.Codigo
            FROM Estadias e JOIN TicketsAcceso t ON t.EstadiaId=e.Id
            LEFT JOIN OcupacionesPlaza o ON o.EstadiaId=e.Id
            WHERE e.Ticket=$numero;
            """;
        cmd.Parameters.AddWithValue("$numero", numero);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("No existe el ticket solicitado.");
        return new TicketIngreso(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            reader.GetString(6),
            reader.IsDBNull(7)?null:reader.GetString(7));
    }

    public void RegistrarImpresion(Guid estadiaId, string impresora, bool enviado, string detalle)
    {
        ExigirPermiso(PermisosAparcar.Impresion);
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ImpresionesTicket(Id,EstadiaId,FechaUtc,Impresora,Resultado,Detalle)
            VALUES($id,$estadia,$fecha,$impresora,$resultado,$detalle);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$estadia", estadiaId.ToString("D"));
        cmd.Parameters.AddWithValue("$fecha", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$impresora", impresora);
        cmd.Parameters.AddWithValue("$resultado", enviado ? "Enviado" : "Error");
        cmd.Parameters.AddWithValue("$detalle", detalle);
        cmd.ExecuteNonQuery();
    }

    public string? ObtenerPreferencia(string clave)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Valor FROM PreferenciasDispositivos WHERE Clave=$clave";
        cmd.Parameters.AddWithValue("$clave", clave);
        return cmd.ExecuteScalar() as string;
    }

    public void GuardarPreferencia(string clave, string valor)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PreferenciasDispositivos(Clave,Valor) VALUES($clave,$valor)
            ON CONFLICT(Clave) DO UPDATE SET Valor=excluded.Valor;
            """;
        cmd.Parameters.AddWithValue("$clave", clave);
        cmd.Parameters.AddWithValue("$valor", valor);
        cmd.ExecuteNonQuery();
    }
}
