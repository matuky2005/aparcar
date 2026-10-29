using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// Cierre Z operativo: resumen numerado e inmutable de un turno cerrado.
/// No es una declaración fiscal ni reemplaza los comprobantes de ARCA.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV26CierresZ()
    {
        using (var db = Abrir())
        using (var consulta = db.CreateCommand())
        {
            consulta.CommandText = "SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=20";
            if (Convert.ToInt64(consulta.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        CrearRespaldoInterno();
        using var dbMigrar = Abrir();
        using var tx = dbMigrar.BeginTransaction();
        using var cmd = dbMigrar.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CierresZ(
                Id TEXT PRIMARY KEY,
                Numero INTEGER NOT NULL UNIQUE,
                TurnoId TEXT NOT NULL UNIQUE REFERENCES TurnosCaja(Id),
                Operador TEXT NOT NULL,
                AperturaUtc TEXT NOT NULL,
                CierreUtc TEXT NOT NULL,
                RecaudacionEfectivoCentavos INTEGER NOT NULL,
                EsperadoCentavos INTEGER NOT NULL,
                ContadoCentavos INTEGER NOT NULL,
                DiferenciaCentavos INTEGER NOT NULL,
                CreadoUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(20,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static long SiguienteNumeroCierreZ(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(Numero),0)+1 FROM CierresZ";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void RegistrarCierreZ(
        SqliteConnection db, SqliteTransaction tx, TurnoCajaOperativo turno,
        long contado, long diferencia, DateTimeOffset fecha, out long numero)
    {
        numero = SiguienteNumeroCierreZ(db, tx);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO CierresZ(
                Id,Numero,TurnoId,Operador,AperturaUtc,CierreUtc,
                RecaudacionEfectivoCentavos,EsperadoCentavos,ContadoCentavos,
                DiferenciaCentavos,CreadoUtc)
            VALUES($id,$numero,$turno,$operador,$apertura,$cierre,
                $recaudacion,$esperado,$contado,$diferencia,$creado);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$numero", numero);
        cmd.Parameters.AddWithValue("$turno", turno.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$operador", turno.Operador);
        cmd.Parameters.AddWithValue("$apertura", turno.AperturaUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$cierre", fecha.ToString("O"));
        cmd.Parameters.AddWithValue("$recaudacion", turno.CobradoCentavos);
        cmd.Parameters.AddWithValue("$esperado", turno.EsperadoCentavos);
        cmd.Parameters.AddWithValue("$contado", contado);
        cmd.Parameters.AddWithValue("$diferencia", diferencia);
        cmd.Parameters.AddWithValue("$creado", fecha.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CierreZListado> ListarCierresZ(int max = 100)
    {
        ExigirPermiso(PermisosAparcar.Reportes);
        if (max is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(max));
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Numero,TurnoId,Operador,AperturaUtc,CierreUtc,
                   RecaudacionEfectivoCentavos,EsperadoCentavos,
                   ContadoCentavos,DiferenciaCentavos
            FROM CierresZ ORDER BY Numero DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max", max);
        using var reader = cmd.ExecuteReader();
        var lista = new List<CierreZListado>();
        while (reader.Read())
        {
            lista.Add(new CierreZListado(
                reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)));
        }
        return lista;
    }
}
