using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// Recibo no fiscal asociado a un único cobro. El ticket de acceso nunca se
/// considera comprobante de pago y el envío al spooler no acredita papel impreso.
/// </summary>
public sealed record ReciboCobroV21(
    CobroConfirmadoV4 Cobro, bool Devuelto, bool TieneIntentoOriginal,
    string? EstadoUltimaImpresion);

/// <summary>
/// Historial de intentos de impresión de recibos de estacionamiento.
/// Es independiente de los tickets de acceso, de Ticket X y de WSFE.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV21RecibosCobro()
    {
        using(var cn=Abrir())
        using(var consulta=cn.CreateCommand())
        {
            consulta.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=17";
            if(Convert.ToInt64(consulta.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS ImpresionesReciboCobro(
                Id TEXT PRIMARY KEY,
                CobroId TEXT NOT NULL REFERENCES CobrosCaja(Id),
                EsCopia INTEGER NOT NULL CHECK(EsCopia IN(0,1)),
                Motivo TEXT NOT NULL,
                Impresora TEXT NOT NULL,
                Estado TEXT NOT NULL CHECK(Estado IN('Pendiente','Enviado','Error')),
                Detalle TEXT NOT NULL,
                Actor TEXT NOT NULL,
                CreadaUtc TEXT NOT NULL,
                TerminadaUtc TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ReciboCobro_Original
                ON ImpresionesReciboCobro(CobroId) WHERE EsCopia=0;
            CREATE INDEX IF NOT EXISTS IX_ReciboCobro_Cobro
                ON ImpresionesReciboCobro(CobroId,CreadaUtc);
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(17,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private const string ColumnasReciboV21="""
        p.Id,p.EstadiaId,p.Ticket,p.ImporteCentavos,p.EntregadoCentavos,
        p.VueltoCentavos,p.FechaUtc,m.MedioPago,m.Referencia,
        EXISTS(SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.Id),
        EXISTS(SELECT 1 FROM ImpresionesReciboCobro i
                WHERE i.CobroId=p.Id AND i.EsCopia=0),
        (SELECT i.Estado FROM ImpresionesReciboCobro i
         WHERE i.CobroId=p.Id ORDER BY i.CreadaUtc DESC,i.rowid DESC LIMIT 1)
        """;

    private static ReciboCobroV21 LeerReciboV21(SqliteDataReader r)
    {
        var cobro=new CobroConfirmadoV4(
            Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetInt64(2),
            r.GetString(7),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),
            r.GetString(8),DateTimeOffset.Parse(r.GetString(6),
                CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
        return new ReciboCobroV21(cobro,r.GetInt64(9)!=0,r.GetInt64(10)!=0,
            r.IsDBNull(11)?null:r.GetString(11));
    }

    public ReciboCobroV21 ObtenerReciboCobroV21(Guid cobroId)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(cobroId==Guid.Empty)throw new ArgumentException("Identificador de cobro obligatorio.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"""
            SELECT {ColumnasReciboV21}
            FROM CobrosCaja p JOIN CobroMedios m ON m.CobroId=p.Id
            WHERE p.Id=$id;
            """;
        cmd.Parameters.AddWithValue("$id",cobroId.ToString("D"));
        using var r=cmd.ExecuteReader();
        return r.Read()?LeerReciboV21(r):
            throw new InvalidOperationException("No se encontró un cobro confirmado.");
    }

    public IReadOnlyList<ReciboCobroV21> ListarRecibosCobroV21(int max=100)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"""
            SELECT {ColumnasReciboV21}
            FROM CobrosCaja p JOIN CobroMedios m ON m.CobroId=p.Id
            ORDER BY p.FechaUtc DESC,p.rowid DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<ReciboCobroV21>();
        while(r.Read())lista.Add(LeerReciboV21(r));
        return lista;
    }

    /// <summary>
    /// Registrar primero, imprimir después. Una sola orden ORIGINAL por cobro.
    /// Después de un error, la recuperación será COPIA con motivo autorizado.
    /// </summary>
    public Guid RegistrarIntentoReciboCobroV21(Guid cobroId,bool copia,string motivo,string impresora)
    {
        ExigirPermiso(copia?PermisosAparcar.Reimpresion:PermisosAparcar.Impresion);
        if(cobroId==Guid.Empty)throw new ArgumentException("Identificador de cobro obligatorio.");
        motivo=(motivo??"").Trim();
        if(copia && motivo.Length is <8 or >250)
            throw new ArgumentException("La copia exige motivo de 8 a 250 caracteres.");
        if(impresora is null || impresora.Trim().Length is <1 or >200)
            throw new ArgumentException("Configurá una impresora de recibos.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using(var existe=db.CreateCommand())
        {
            existe.Transaction=tx;
            existe.CommandText="SELECT COUNT(*) FROM CobrosCaja WHERE Id=$cobro";
            existe.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            if(Convert.ToInt64(existe.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("El cobro no está confirmado o no existe.");
        }
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO ImpresionesReciboCobro
                    (Id,CobroId,EsCopia,Motivo,Impresora,Estado,Detalle,Actor,CreadaUtc)
                VALUES($id,$cobro,$copia,$motivo,$impresora,'Pendiente','',
                    $actor,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$copia",copia?1:0);
            cmd.Parameters.AddWithValue("$motivo",copia?motivo:"");
            cmd.Parameters.AddWithValue("$impresora",impresora.Trim());
            cmd.Parameters.AddWithValue("$actor",SesionActual?.Usuario??"");
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            try{cmd.ExecuteNonQuery();}
            catch(SqliteException ex) when(ex.SqliteErrorCode==19 && !copia)
            {
                throw new InvalidOperationException(
                    "Este cobro ya tiene un intento ORIGINAL. No se imprimió de nuevo; "+
                    "solicitá COPIA autorizada para recuperarlo.",ex);
            }
        }
        AuditarSesion(db,tx,copia?"ReciboCobro.CopiaSolicitada":"ReciboCobro.OriginalSolicitado",
            cobroId.ToString("D"),$"Intento={id:D}; copia={copia}; motivo={motivo}");
        tx.Commit();
        return id;
    }

    public void RegistrarResultadoReciboCobroV21(Guid intentoId,bool enviado,string detalle)
    {
        if(intentoId==Guid.Empty)throw new ArgumentException("Identificador de impresión obligatorio.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        bool copia;
        Guid cobroId;
        using(var leer=db.CreateCommand())
        {
            leer.Transaction=tx;
            leer.CommandText="SELECT CobroId,EsCopia FROM ImpresionesReciboCobro WHERE Id=$id";
            leer.Parameters.AddWithValue("$id",intentoId.ToString("D"));
            using var r=leer.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Intento de impresión inexistente.");
            cobroId=Guid.Parse(r.GetString(0));
            copia=r.GetInt64(1)==1;
        }
        ExigirPermiso(copia?PermisosAparcar.Reimpresion:PermisosAparcar.Impresion);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE ImpresionesReciboCobro
                SET Estado=$estado,Detalle=$detalle,TerminadaUtc=$fecha
                WHERE Id=$id AND Estado='Pendiente';
                """;
            cmd.Parameters.AddWithValue("$estado",enviado?"Enviado":"Error");
            cmd.Parameters.AddWithValue("$detalle",(detalle??"").Length>500?detalle[..500]:detalle??"");
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id",intentoId.ToString("D"));
            if(cmd.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("La impresión ya fue resuelta; no se altera.");
        }
        AuditarSesion(db,tx,"ReciboCobro.ImpresionResultado",cobroId.ToString("D"),
            $"Intento={intentoId:D}; estado={(enviado?"Enviado":"Error")}");
        tx.Commit();
    }
}
