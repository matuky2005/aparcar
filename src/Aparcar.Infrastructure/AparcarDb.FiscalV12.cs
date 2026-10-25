using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed record SolicitudFiscalPrueba(
    Guid Id,string Origen,Guid CobroId,string CuitEmisor,
    ComprobanteFiscalWsfe Comprobante,string Estado,string? Cae,DateOnly? Vencimiento,
    string? Observacion);

/// <summary>
/// Todas las autorizaciones son exclusivamente de homologación.
/// Una solicitud de red ambigua queda bloqueada hasta FECompConsultar.
/// No permite editar o reemitir automáticamente una solicitud ya reservada.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV12Fiscal()
    {
        using(var cn=Abrir())
        using(var c=cn.CreateCommand())
        {
            c.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=12";
            if(Convert.ToInt64(c.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS FacturasHomologacion(
                Id TEXT PRIMARY KEY,
                Origen TEXT NOT NULL CHECK(Origen IN('Estacionamiento','Lavadero','Abono')),
                CobroId TEXT NOT NULL,
                CuitEmisor TEXT NOT NULL,
                PuntoVenta INTEGER NOT NULL,
                Tipo INTEGER NOT NULL,
                Numero INTEGER NOT NULL,
                DatosJson TEXT NOT NULL,
                HuellaSha256 TEXT NOT NULL,
                Estado TEXT NOT NULL CHECK(Estado IN(
                    'Preparada','PorConciliar','Autorizada','Rechazada')),
                Cae TEXT,
                CaeVto TEXT,
                Observacion TEXT,
                CreadaUtc TEXT NOT NULL,
                EnviadaUtc TEXT,
                ActualizadaUtc TEXT NOT NULL,
                UNIQUE(Origen,CobroId),
                UNIQUE(CuitEmisor,PuntoVenta,Tipo,Numero)
            );
            CREATE INDEX IF NOT EXISTS IX_FacturaHomo_Estado
                ON FacturasHomologacion(Estado,PuntoVenta,Tipo);
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(12,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string Huella(string valor)=>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor)));

    public SolicitudFiscalPrueba PrepararFacturaHomologacion(
        string cuitEmisor,string origen,Guid cobroId,ComprobanteFiscalWsfe comprobante)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(!AdaptadorWsfeFacturador.CuitValido(cuitEmisor))
            throw new ArgumentException("El CUIT emisor no supera el dígito verificador.");
        AdaptadorWsfeFacturador.Validar(comprobante);
        if(origen is not ("Estacionamiento" or "Lavadero" or "Abono"))
            throw new ArgumentException("Origen fiscal inválido.");
        if(cobroId==Guid.Empty)throw new ArgumentException("Cobro inválido.");
        if(comprobante.Fecha > DateOnly.FromDateTime(DateTime.Now.AddDays(1)))
            throw new InvalidOperationException("No se puede preparar una factura futura.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        long centavos;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT ImporteCentavos FROM PendientesFiscales
                WHERE Origen=$origen AND CobroId=$cobro AND Estado='PendienteDatosFiscales';
                """;
            cmd.Parameters.AddWithValue("$origen",origen);
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            var dato=cmd.ExecuteScalar();
            if(dato is null)
                throw new InvalidOperationException(
                    "El cobro no existe, no es positivo o está sujeto a una devolución/revisión.");
            centavos=Convert.ToInt64(dato,CultureInfo.InvariantCulture);
        }
        if(comprobante.Total!=centavos/100m)
            throw new InvalidOperationException("El total fiscal debe coincidir exactamente con el cobro.");
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COUNT(*) FROM FacturasHomologacion
                WHERE CuitEmisor=$cuit AND PuntoVenta=$pv AND Tipo=$tipo
                  AND Estado IN('Preparada','PorConciliar');
                """;
            cmd.Parameters.AddWithValue("$cuit",cuitEmisor);
            cmd.Parameters.AddWithValue("$pv",comprobante.PuntoVenta);
            cmd.Parameters.AddWithValue("$tipo",comprobante.Tipo);
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException(
                    "Ya existe un comprobante en preparación o pendiente de conciliación para este CUIT/PV/tipo.");
        }
        var fecha=DateTimeOffset.UtcNow.ToString("O");
        var serializado=JsonSerializer.Serialize(comprobante);
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO FacturasHomologacion(Id,Origen,CobroId,CuitEmisor,PuntoVenta,Tipo,
                    Numero,DatosJson,HuellaSha256,Estado,CreadaUtc,ActualizadaUtc)
                VALUES($id,$origen,$cobro,$cuit,$pv,$tipo,$numero,$datos,$hash,
                    'Preparada',$fecha,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$origen",origen);
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$cuit",cuitEmisor);
            cmd.Parameters.AddWithValue("$pv",comprobante.PuntoVenta);
            cmd.Parameters.AddWithValue("$tipo",comprobante.Tipo);
            cmd.Parameters.AddWithValue("$numero",comprobante.Numero);
            cmd.Parameters.AddWithValue("$datos",serializado);
            cmd.Parameters.AddWithValue("$hash",Huella(serializado));
            cmd.Parameters.AddWithValue("$fecha",fecha);
            try{cmd.ExecuteNonQuery();}
            catch(SqliteException e) when(e.SqliteErrorCode==19)
            {
                throw new InvalidOperationException(
                    "Ya hay una solicitud para este cobro o número fiscal. No se duplicó.",e);
            }
        }
        AuditarSesion(db,tx,"Fiscal.HomologacionPreparar",id.ToString("D"),
            $"Origen={origen}; cobro={cobroId:D}; PV={comprobante.PuntoVenta}; tipo={comprobante.Tipo}; número={comprobante.Numero}; hash={Huella(serializado)}");
        tx.Commit();
        return new SolicitudFiscalPrueba(id,origen,cobroId,cuitEmisor,comprobante,
            "Preparada",null,null,null);
    }

    private static SolicitudFiscalPrueba LeerFactura(SqliteDataReader r)
    {
        var json=r.GetString(4);
        if(!string.Equals(Huella(json),r.GetString(5),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Los datos fiscales fueron modificados fuera de Aparcar.");
        var data=JsonSerializer.Deserialize<ComprobanteFiscalWsfe>(json) ??
            throw new InvalidOperationException("No se pudieron recuperar los datos fiscales.");
        return new SolicitudFiscalPrueba(
            Guid.Parse(r.GetString(0)),r.GetString(1),Guid.Parse(r.GetString(2)),
            r.GetString(3),data,r.GetString(6),
            r.IsDBNull(7)?null:r.GetString(7),
            r.IsDBNull(8)?null:DateOnly.ParseExact(r.GetString(8),"yyyyMMdd",CultureInfo.InvariantCulture),
            r.IsDBNull(9)?null:r.GetString(9));
    }

    private const string ColumnasFactura="""
        Id,Origen,CobroId,CuitEmisor,DatosJson,HuellaSha256,Estado,Cae,CaeVto,Observacion
        """;

    public IReadOnlyList<SolicitudFiscalPrueba> ListarFacturasHomologacion(int max=100)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasFactura} FROM FacturasHomologacion ORDER BY CreadaUtc DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<SolicitudFiscalPrueba>();
        while(r.Read())lista.Add(LeerFactura(r));
        return lista;
    }

    public SolicitudFiscalPrueba ObtenerFacturaHomologacion(Guid id)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasFactura} FROM FacturasHomologacion WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        using var r=cmd.ExecuteReader();
        return r.Read()?LeerFactura(r):throw new InvalidOperationException("Solicitud fiscal inexistente.");
    }

    /// <summary>
    /// Persistir ANTES de hacer FECAESolicitar. Si se corta la luz, obliga
    /// a consultar en ARCA y jamás permite una segunda emisión automática.
    /// </summary>
    public SolicitudFiscalPrueba MarcarEnvioHomologacion(Guid id)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        using var db=Abrir();using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE FacturasHomologacion SET Estado='PorConciliar',
                    EnviadaUtc=$ahora,ActualizadaUtc=$ahora,
                    Observacion='Solicitud marcada antes de enviar; no reintentar sin FECompConsultar'
                WHERE Id=$id AND Estado='Preparada'
                  AND EXISTS (
                    SELECT 1 FROM PendientesFiscales p
                    WHERE p.Origen=FacturasHomologacion.Origen
                      AND p.CobroId=FacturasHomologacion.CobroId
                      AND p.Estado='PendienteDatosFiscales'
                  );
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$ahora",DateTimeOffset.UtcNow.ToString("O"));
            if(cmd.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("Solicitud ya enviada o bloqueada. Consultá ARCA antes de reintentar.");
        }
        AuditarSesion(db,tx,"Fiscal.HomologacionEnvio",id.ToString("D"),
            "Paso irreversible: pasar a PorConciliar antes del envío");
        tx.Commit();
        return ObtenerFacturaHomologacion(id);
    }

    public SolicitudFiscalPrueba RegistrarRespuestaHomologacion(
        Guid id,ResultadoAutorizacionWsfe resultado)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        var antes=ObtenerFacturaHomologacion(id);
        if(antes.Estado!="PorConciliar")
            throw new InvalidOperationException("No se espera una respuesta para este estado.");
        var estado=resultado.Estado switch
        {
            EstadoAutorizacionWsfe.Autorizado=>"Autorizada",
            EstadoAutorizacionWsfe.Rechazado=>"Rechazada",
            _=>"PorConciliar"
        };
        if(estado=="Autorizada"&&(resultado.Cae?.Length!=14||
           !resultado.Cae.All(char.IsDigit)||resultado.Vencimiento is null))
            throw new InvalidOperationException("No se puede registrar una autorización sin CAE y vencimiento válidos.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE FacturasHomologacion
                SET Estado=$estado,Cae=$cae,CaeVto=$vto,Observacion=$observacion,
                    ActualizadaUtc=$fecha
                WHERE Id=$id AND Estado='PorConciliar';
                """;
            cmd.Parameters.AddWithValue("$estado",estado);
            cmd.Parameters.AddWithValue("$cae",resultado.Estado==EstadoAutorizacionWsfe.Autorizado?
                resultado.Cae!:(object)DBNull.Value);
            cmd.Parameters.AddWithValue("$vto",resultado.Estado==EstadoAutorizacionWsfe.Autorizado?
                resultado.Vencimiento!.Value.ToString("yyyyMMdd",CultureInfo.InvariantCulture):(object)DBNull.Value);
            cmd.Parameters.AddWithValue("$observacion",resultado.Detalle.Length>400?
                resultado.Detalle[..400]:resultado.Detalle);
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("El estado cambió durante la operación.");
        }
        AuditarSesion(db,tx,"Fiscal.HomologacionRespuesta",id.ToString("D"),
            $"Estado={estado}; {(estado=="Autorizada"?"CAE verificado":"sin CAE")}");
        tx.Commit();
        return ObtenerFacturaHomologacion(id);
    }

    public SolicitudFiscalPrueba RegistrarConciliacionHomologacion(
        Guid id,ResultadoConsultaWsfe consulta)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        var actual=ObtenerFacturaHomologacion(id);
        if(actual.Estado!="PorConciliar")
            throw new InvalidOperationException("Solo se concilian solicitudes ambiguas.");
        if(!consulta.Encontrado || !consulta.Coincide || !consulta.TieneCae)
            throw new InvalidOperationException(
                "No se comprobó identidad y CAE oficiales. La solicitud permanece bloqueada; no reemitir.");
        return RegistrarRespuestaHomologacion(id,
            new ResultadoAutorizacionWsfe(EstadoAutorizacionWsfe.Autorizado,
                consulta.Cae,consulta.Vencimiento,Array.Empty<int>(),
                "Autorización conciliada mediante FECompConsultar"));
    }
}
