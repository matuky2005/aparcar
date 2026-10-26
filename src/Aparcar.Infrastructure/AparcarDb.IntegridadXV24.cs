using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// v0.24: barrera persistente contra dos impresiones ORIGINAL del mismo X.
/// Se usa trigger en vez de índice UNIQUE para conservar posibles intentos
/// originales duplicados heredados de versiones previas: no se borra auditoría.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV24IntegridadX()
    {
        using(var db=Abrir())
        using(var consulta=db.CreateCommand())
        {
            consulta.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=18";
            if(Convert.ToInt64(consulta.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var dbMigrar=Abrir();
        using var tx=dbMigrar.BeginTransaction();
        using var cmd=dbMigrar.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TRIGGER IF NOT EXISTS TR_TicketX_OriginalUnico
            BEFORE INSERT ON ImpresionesX
            WHEN NEW.EsCopia=0 AND EXISTS (
                 SELECT 1 FROM ImpresionesX prev
                 WHERE prev.TicketXId=NEW.TicketXId AND prev.EsCopia=0)
            BEGIN
                SELECT RAISE(ABORT,'El ticket X ya registra un intento ORIGINAL; se necesita COPIA autorizada.');
            END;
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(18,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public bool TicketXTieneIntentoOriginal(Guid ticketId)
    {
        ExigirPermiso(Aparcar.Domain.PermisosAparcar.Consulta);
        if(ticketId==Guid.Empty)
            throw new ArgumentException("El ticket X debe estar identificado.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT EXISTS(
                SELECT 1 FROM ImpresionesX
                WHERE TicketXId=$id AND EsCopia=0);
            """;
        cmd.Parameters.AddWithValue("$id",ticketId.ToString("D"));
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;
    }
}
