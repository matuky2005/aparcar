using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    /// <summary>
    /// Esquema 13: extensión no destructiva de facturas de prueba v0.15.
    /// No altera los estados de WSFE ni prepara facturación productiva.
    /// </summary>
    private void InicializarV15Fiscal()
    {
        using(var consulta=Abrir())
        using(var cmd=consulta.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=13";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmdCrear=db.CreateCommand();
        cmdCrear.Transaction=tx;
        cmdCrear.CommandText="""
            CREATE TABLE IF NOT EXISTS DatosDocumentosFiscalesPrueba(
                SolicitudId TEXT PRIMARY KEY NOT NULL
                    REFERENCES FacturasHomologacion(Id),
                DatosJson TEXT NOT NULL,
                HuellaSha256 TEXT NOT NULL,
                RegistradoUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(13,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmdCrear.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Una única captura inmutable antes de contactar ARCA.
    /// Si se cometió un error hay que resolver la solicitud sin editar historial.
    /// </summary>
    public void RegistrarDatosDocumentoFiscalPrueba(
        Guid solicitudId,DatosDocumentoFiscalPrueba datos)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();
        using var tx=db.BeginTransaction();

        SolicitudFiscalPrueba solicitud;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText=$"SELECT {ColumnasFactura} FROM FacturasHomologacion WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",solicitudId.ToString("D"));
            using var lector=cmd.ExecuteReader();
            solicitud=lector.Read()?LeerFactura(lector):
                throw new InvalidOperationException("No existe la solicitud fiscal.");
        }
        ValidadorDocumentoFiscalPrueba.Validar(datos,solicitud);
        var json=JsonSerializer.Serialize(datos);
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO DatosDocumentosFiscalesPrueba
                    (SolicitudId,DatosJson,HuellaSha256,RegistradoUtc)
                SELECT $id,$json,$hash,$fecha
                WHERE EXISTS(
                    SELECT 1 FROM FacturasHomologacion
                    WHERE Id=$id AND Estado='Preparada')
                  AND NOT EXISTS(
                    SELECT 1 FROM DatosDocumentosFiscalesPrueba WHERE SolicitudId=$id);
                """;
            cmd.Parameters.AddWithValue("$id",solicitudId.ToString("D"));
            cmd.Parameters.AddWithValue("$json",json);
            cmd.Parameters.AddWithValue("$hash",Huella(json));
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            if(cmd.ExecuteNonQuery()!=1)
                throw new InvalidOperationException(
                    "La solicitud ya posee datos congelados o cambió de estado; no se sobrescribió.");
        }
        AuditarSesion(db,tx,"Fiscal.HomologacionIdentidad",solicitudId.ToString("D"),
            $"Identidad comercial capturada antes del envío; hash={Huella(json)}");
        tx.Commit();
    }

    public DatosDocumentoFiscalPrueba? ObtenerDatosDocumentoFiscalPrueba(Guid solicitudId)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT DatosJson,HuellaSha256 FROM DatosDocumentosFiscalesPrueba
            WHERE SolicitudId=$id;
            """;
        cmd.Parameters.AddWithValue("$id",solicitudId.ToString("D"));
        using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        var json=r.GetString(0);
        if(!string.Equals(Huella(json),r.GetString(1),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Los datos comerciales del comprobante fueron modificados fuera de Aparcar.");
        return JsonSerializer.Deserialize<DatosDocumentoFiscalPrueba>(json) ??
            throw new InvalidOperationException("No fue posible leer el snapshot fiscal.");
    }
}
