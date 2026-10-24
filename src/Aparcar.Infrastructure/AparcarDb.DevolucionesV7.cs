using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    public IReadOnlyList<(Guid CobroId,string Categoria,string Patente,long ImporteCentavos)> ListarComplementosReembolsables()
    {
        ExigirPermiso(PermisosAparcar.Devolucion);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT c.Id,c.Categoria,COALESCE(l.Patente,a.Patente),c.ImporteCentavos
            FROM CobrosComplementarios c
            LEFT JOIN OrdenesLavado l ON c.Categoria='Lavadero' AND l.Id=c.ReferenciaId
            LEFT JOIN ContratosAbono a ON c.Categoria='Abono' AND a.Id=c.ReferenciaId
            WHERE c.MedioPago='Efectivo'
              AND NOT EXISTS(SELECT 1 FROM DevolucionesComplementarias d WHERE d.CobroId=c.Id)
            ORDER BY c.FechaUtc DESC LIMIT 150;
            """;
        var lista=new List<(Guid,string,string,long)>();
        using var r=cmd.ExecuteReader();
        while(r.Read())lista.Add((Guid.Parse(r.GetString(0)),r.GetString(1),
            r.GetString(2),r.GetInt64(3)));
        return lista;
    }

    public DevolucionComplementaria DevolverComplementoEfectivo(
        Guid cobroId,string motivo,string supervisor,string pin)
    {
        ExigirPermiso(PermisosAparcar.Devolucion);
        if(string.IsNullOrWhiteSpace(motivo)||motivo.Trim().Length is < 8 or > 300)
            throw new ArgumentException("Indicá un motivo de 8 a 300 caracteres.");
        if(string.IsNullOrWhiteSpace(supervisor)||supervisor.Trim().Length>80)
            throw new ArgumentException("Identificá al supervisor.");
        if(string.IsNullOrWhiteSpace(pin))
            throw new InvalidOperationException("Se requiere PIN de supervisor.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        if(!VerificarPin(db,tx,pin))throw new InvalidOperationException("PIN incorrecto.");
        var turno=ResumenInterno(db,tx) ??
            throw new InvalidOperationException("Abrí una caja antes de devolver.");
        string categoria;Guid referencia;long importe;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Categoria,ReferenciaId,ImporteCentavos
                FROM CobrosComplementarios c
                WHERE c.Id=$id AND c.MedioPago='Efectivo'
                  AND NOT EXISTS(SELECT 1 FROM DevolucionesComplementarias d
                    WHERE d.CobroId=c.Id);
                """;
            cmd.Parameters.AddWithValue("$id",cobroId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("El cobro no existe, no es efectivo o ya fue devuelto.");
            categoria=r.GetString(0);
            referencia=Guid.Parse(r.GetString(1));
            importe=r.GetInt64(2);
        }
        if(turno.EfectivoEsperadoCentavos<importe)
            throw new InvalidOperationException("Efectivo esperado insuficiente para la devolución.");
        if(categoria=="Lavadero")
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Estado FROM OrdenesLavado WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",referencia.ToString("D"));
            if(cmd.ExecuteScalar() as string == EstadosLavado.Entregado)
                throw new InvalidOperationException("No se admite devolución automática de un lavado entregado.");
        }
        else if(categoria=="Abono")
        {
            // No se puede cancelar un abono que pudo cubrir una estadía desde el
            // inicio de vigencia (incluidas las que siguen abiertas).
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COUNT(*) FROM Estadias e
                JOIN AbonoVehiculos v ON v.Patente=e.PatenteNormalizada
                JOIN ContratosAbono c ON c.Id=v.ContratoId
                WHERE c.Id=$id AND c.Estado='Activo'
                  AND e.TipoVehiculo=v.TipoVehiculo
                  AND e.IngresoUtc>=c.InicioUtc AND e.IngresoUtc<c.FinUtc;
                """;
            cmd.Parameters.AddWithValue("$id",referencia.ToString("D"));
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("No se puede devolver un abono ya potencialmente utilizado por un vehículo.");
        }
        var fecha=DateTimeOffset.UtcNow;
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO DevolucionesComplementarias(
                    Id,CobroId,TurnoId,Categoria,ReferenciaId,ImporteCentavos,FechaUtc,Motivo,AutorizadoPor)
                VALUES($id,$cobro,$turno,$categoria,$referencia,$importe,$fecha,$motivo,$supervisor);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$turno",turno.TurnoId.ToString("D"));
            cmd.Parameters.AddWithValue("$categoria",categoria);
            cmd.Parameters.AddWithValue("$referencia",referencia.ToString("D"));
            cmd.Parameters.AddWithValue("$importe",importe);
            cmd.Parameters.AddWithValue("$fecha",Fecha(fecha));
            cmd.Parameters.AddWithValue("$motivo",motivo.Trim());
            cmd.Parameters.AddWithValue("$supervisor",supervisor.Trim());
            cmd.ExecuteNonQuery();
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText=categoria=="Lavadero"
                ? "UPDATE OrdenesLavado SET Estado='Cancelado',ActualizadoUtc=$fecha WHERE Id=$id AND Estado!='Entregado'"
                : "UPDATE ContratosAbono SET Estado='Cancelado' WHERE Id=$id AND Estado='Activo'";
            cmd.Parameters.AddWithValue("$id",referencia.ToString("D"));
            cmd.Parameters.AddWithValue("$fecha",Fecha(fecha));
            if(cmd.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("La operación no pudo cancelarse. Se revierte la devolución.");
        }
        AuditarSesion(db,tx,"Complemento.Devolucion",cobroId.ToString("D"),
            $"Categoría={categoria}; importe={importe}; supervisor={supervisor.Trim()}; motivo={motivo.Trim()}");
        tx.Commit();
        return new DevolucionComplementaria(id,cobroId,turno.TurnoId,
            categoria,referencia,importe,fecha,motivo.Trim(),supervisor.Trim());
    }
}
