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

public sealed record NotaCreditoFiscalPrueba(
    Guid Id, Guid FacturaOriginalId, string Origen, Guid CobroId,
    string CuitEmisor, ComprobanteFiscalWsfe Comprobante,
    string Estado, string? Cae, DateOnly? Vencimiento, string? Observacion);

public sealed partial class AparcarDb
{
    private void InicializarV27NotasCredito()
    {
        using(var consulta=Abrir())
        using(var cmd=consulta.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=21";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var cmdCrear=db.CreateCommand();cmdCrear.Transaction=tx;
        cmdCrear.CommandText="""
            CREATE TABLE IF NOT EXISTS NotasCreditoHomologacion(
                Id TEXT PRIMARY KEY,
                FacturaOriginalId TEXT NOT NULL REFERENCES FacturasHomologacion(Id),
                Origen TEXT NOT NULL,
                CobroId TEXT NOT NULL,
                CuitEmisor TEXT NOT NULL,
                PuntoVenta INTEGER NOT NULL,
                Tipo INTEGER NOT NULL,
                Numero INTEGER NOT NULL,
                DatosJson TEXT NOT NULL,
                HuellaSha256 TEXT NOT NULL,
                Estado TEXT NOT NULL CHECK(Estado IN('Preparada','PorConciliar','Autorizada','Rechazada')),
                Cae TEXT, CaeVto TEXT, Observacion TEXT,
                CreadaUtc TEXT NOT NULL, EnviadaUtc TEXT, ActualizadaUtc TEXT NOT NULL,
                UNIQUE(CuitEmisor,PuntoVenta,Tipo,Numero)
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(21,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmdCrear.ExecuteNonQuery();tx.Commit();
    }

    public NotaCreditoFiscalPrueba PrepararNotaCreditoHomologacion(
        Guid facturaOriginalId,ComprobanteFiscalWsfe nota)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        var original=ObtenerFacturaHomologacion(facturaOriginalId);
        if(original.Estado!="Autorizada" || string.IsNullOrWhiteSpace(original.Cae))
            throw new InvalidOperationException("Solo se puede preparar una nota sobre una factura autorizada con CAE.");
        if(nota.ComprobantesAsociados is not { Count:1 } asociado ||
           asociado[0].PuntoVenta!=original.Comprobante.PuntoVenta ||
           asociado[0].Tipo!=original.Comprobante.Tipo ||
           asociado[0].Numero!=original.Comprobante.Numero)
            throw new InvalidOperationException("La nota no coincide con la factura original seleccionada.");
        var tipoEsperado=original.Comprobante.Tipo switch {1=>3,6=>8,11=>13,_=>0};
        if(nota.Tipo!=tipoEsperado)
            throw new InvalidOperationException("La clase de la nota no coincide con la factura original.");
        AdaptadorWsfeFacturador.Validar(nota);
        if(nota.Total>original.Comprobante.Total)
            throw new InvalidOperationException("La nota de crédito no puede superar el total original.");
        var json=JsonSerializer.Serialize(nota);var ahora=DateTimeOffset.UtcNow.ToString("O");
        var id=Guid.NewGuid();
        using var db=Abrir();using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO NotasCreditoHomologacion
                (Id,FacturaOriginalId,Origen,CobroId,CuitEmisor,PuntoVenta,Tipo,Numero,
                 DatosJson,HuellaSha256,Estado,CreadaUtc,ActualizadaUtc)
            VALUES($id,$original,$origen,$cobro,$cuit,$pv,$tipo,$numero,$datos,$hash,
                   'Preparada',$ahora,$ahora);
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$original",facturaOriginalId.ToString("D"));
        cmd.Parameters.AddWithValue("$origen",original.Origen);
        cmd.Parameters.AddWithValue("$cobro",original.CobroId.ToString("D"));
        cmd.Parameters.AddWithValue("$cuit",original.CuitEmisor);
        cmd.Parameters.AddWithValue("$pv",nota.PuntoVenta);
        cmd.Parameters.AddWithValue("$tipo",nota.Tipo);
        cmd.Parameters.AddWithValue("$numero",nota.Numero);
        cmd.Parameters.AddWithValue("$datos",json);
        cmd.Parameters.AddWithValue("$hash",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        cmd.Parameters.AddWithValue("$ahora",ahora);
        try{cmd.ExecuteNonQuery();}
        catch(SqliteException ex) when(ex.SqliteErrorCode==19)
        { throw new InvalidOperationException("Ya existe una nota de crédito con ese punto de venta y número.",ex); }
        AuditarSesion(db,tx,"Fiscal.NotaCreditoPreparar",id.ToString("D"),
            $"Original={facturaOriginalId:D}; PV={nota.PuntoVenta}; tipo={nota.Tipo}; número={nota.Numero}");
        tx.Commit();
        return new NotaCreditoFiscalPrueba(id,facturaOriginalId,original.Origen,original.CobroId,
            original.CuitEmisor,nota,"Preparada",null,null,null);
    }

    public IReadOnlyList<NotaCreditoFiscalPrueba> ListarNotasCreditoHomologacion(int max=100)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,FacturaOriginalId,Origen,CobroId,CuitEmisor,DatosJson,HuellaSha256,Estado,Cae,CaeVto,Observacion FROM NotasCreditoHomologacion ORDER BY CreadaUtc DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();var lista=new List<NotaCreditoFiscalPrueba>();
        while(r.Read())
        {
            var json=r.GetString(5);
            if(!string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),r.GetString(6),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Datos de nota de crédito alterados.");
            var nota=JsonSerializer.Deserialize<ComprobanteFiscalWsfe>(json) ?? throw new InvalidOperationException("Nota inválida.");
            lista.Add(new NotaCreditoFiscalPrueba(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),Guid.Parse(r.GetString(3)),r.GetString(4),nota,r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:DateOnly.ParseExact(r.GetString(9),"yyyyMMdd",CultureInfo.InvariantCulture),r.IsDBNull(10)?null:r.GetString(10)));
        }
        return lista;
    }
}
