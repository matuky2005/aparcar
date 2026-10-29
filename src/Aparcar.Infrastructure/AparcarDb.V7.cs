using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarV7()
    {
        using (var db=Abrir())
        using (var cmd=db.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=7";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)return;
        }
        CrearRespaldoInterno();
        using var sql=Abrir();using var tx=sql.BeginTransaction();
        // Idempotencia: la versión y todas sus ALTER se aplican juntos.
        using(var cmd=sql.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                ALTER TABLE PlanesAbono ADD COLUMN Modalidad TEXT NOT NULL DEFAULT 'Completa';
                ALTER TABLE ContratosAbono ADD COLUMN Modalidad TEXT NOT NULL DEFAULT 'Completa';
                ALTER TABLE ContratosAbono ADD COLUMN RenovacionDe TEXT REFERENCES ContratosAbono(Id);
                CREATE TABLE IF NOT EXISTS AbonoVehiculos(
                    ContratoId TEXT NOT NULL REFERENCES ContratosAbono(Id),
                    Patente TEXT NOT NULL,
                    TipoVehiculo TEXT NOT NULL,
                    PRIMARY KEY(ContratoId,Patente)
                );
                CREATE INDEX IF NOT EXISTS IX_AbonoVehiculos_Patente
                    ON AbonoVehiculos(Patente,TipoVehiculo);
                INSERT OR IGNORE INTO AbonoVehiculos(ContratoId,Patente,TipoVehiculo)
                SELECT Id,Patente,TipoVehiculo FROM ContratosAbono;
                CREATE TABLE IF NOT EXISTS DevolucionesComplementarias(
                    Id TEXT PRIMARY KEY,
                    CobroId TEXT NOT NULL UNIQUE REFERENCES CobrosComplementarios(Id),
                    TurnoId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                    Categoria TEXT NOT NULL CHECK(Categoria IN('Lavadero','Abono')),
                    ReferenciaId TEXT NOT NULL,
                    ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos>=0),
                    FechaUtc TEXT NOT NULL,
                    Motivo TEXT NOT NULL,
                    AutorizadoPor TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_DevolucionesComplementarias_Turno
                    ON DevolucionesComplementarias(TurnoId);
                INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(7,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<VehiculoAbonado> ListarVehiculosAbono(Guid contratoId)
    {
        ExigirPermiso(PermisosAparcar.AbonosConsultar);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT ContratoId,Patente,TipoVehiculo FROM AbonoVehiculos
            WHERE ContratoId=$id ORDER BY Patente;
            """;
        cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
        var lista=new List<VehiculoAbonado>();
        using var r=cmd.ExecuteReader();
        while(r.Read())lista.Add(new VehiculoAbonado(
            Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2)));
        return lista;
    }

    public void AgregarVehiculoAbono(Guid contratoId,string patente)
    {
        ExigirPermiso(PermisosAparcar.AbonosAdministrar);
        var normalizada=NormalizarPatente(patente);
        if(normalizada.Length is < 3 or > 20)throw new ArgumentException("Patente inválida.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        string estado,tipo;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Estado,TipoVehiculo FROM ContratosAbono WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Contrato inexistente.");
            estado=r.GetString(0);tipo=r.GetString(1);
        }
        if(estado!="Pendiente")throw new InvalidOperationException("Los vehículos se definen antes de activar el abono.");
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM AbonoVehiculos WHERE ContratoId=$id";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>=5)
                throw new InvalidOperationException("Límite provisional: cinco vehículos por contrato.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO AbonoVehiculos(ContratoId,Patente,TipoVehiculo) VALUES($id,$patente,$tipo)";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            cmd.Parameters.AddWithValue("$patente",normalizada);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            try{cmd.ExecuteNonQuery();}
            catch(SqliteException ex) when(ex.SqliteErrorCode==19)
            {throw new InvalidOperationException("La patente ya está autorizada en ese contrato.",ex);}
        }
        AuditarSesion(db,tx,"Abono.VehiculoAgregar",contratoId.ToString("D"),normalizada);
        tx.Commit();
    }

    public void QuitarVehiculoAbono(Guid contratoId,string patente)
    {
        ExigirPermiso(PermisosAparcar.AbonosAdministrar);
        var normalizada=NormalizarPatente(patente);
        using var db=Abrir();using var tx=db.BeginTransaction();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Estado FROM ContratosAbono WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            if(cmd.ExecuteScalar() as string != "Pendiente")
                throw new InvalidOperationException("Solo se editan los vehículos de un contrato pendiente.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM AbonoVehiculos WHERE ContratoId=$id";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)<=1)
                throw new InvalidOperationException("El contrato necesita al menos un vehículo.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="DELETE FROM AbonoVehiculos WHERE ContratoId=$id AND Patente=$patente";
            cmd.Parameters.AddWithValue("$id",contratoId.ToString("D"));
            cmd.Parameters.AddWithValue("$patente",normalizada);
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("La patente no está en el contrato.");
        }
        AuditarSesion(db,tx,"Abono.VehiculoQuitar",contratoId.ToString("D"),normalizada);
        tx.Commit();
    }

    public ContratoAbono CrearRenovacion(Guid contratoAnterior,Guid planId)
    {
        ExigirPermiso(PermisosAparcar.AbonosAdministrar);
        // CrearContratoPendiente maneja transacción propia: copiar patente primaria y
        // luego los adicionales; rollback compensatorio no se necesita porque la
        // renovación queda pendiente y no proporciona cobertura.
        ContratoAbono anterior;
        using(var db=Abrir())
        using(var cmd=db.CreateCommand())
        {
            cmd.CommandText=$"SELECT {ColumnasContrato} FROM ContratosAbono WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id",contratoAnterior.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Contrato anterior inexistente.");
            anterior=LeerContrato(r);
        }
        if(anterior.Estado!="Activo")
            throw new InvalidOperationException("Solo se renuevan contratos activos.");
        var nuevo=CrearContratoPendiente(anterior.Cliente,anterior.Patente,planId);
        try
        {
            using var db=Abrir();using var tx=db.BeginTransaction();
            using(var cmd=db.CreateCommand())
            {
                cmd.Transaction=tx;
                cmd.CommandText="""
                    UPDATE ContratosAbono SET RenovacionDe=$anterior WHERE Id=$nuevo
                    AND TipoVehiculo=(SELECT TipoVehiculo FROM ContratosAbono WHERE Id=$anterior);
                    """;
                cmd.Parameters.AddWithValue("$anterior",contratoAnterior.ToString("D"));
                cmd.Parameters.AddWithValue("$nuevo",nuevo.Id.ToString("D"));
                if(cmd.ExecuteNonQuery()!=1)
                    throw new InvalidOperationException("Para renovar elegí un plan del mismo tipo de vehículo.");
            }
            using(var cmd=db.CreateCommand())
            {
                cmd.Transaction=tx;
                cmd.CommandText="""
                    INSERT OR IGNORE INTO AbonoVehiculos(ContratoId,Patente,TipoVehiculo)
                    SELECT $nuevo,Patente,TipoVehiculo FROM AbonoVehiculos
                    WHERE ContratoId=$anterior AND Patente!=$principal;
                    """;
                cmd.Parameters.AddWithValue("$nuevo",nuevo.Id.ToString("D"));
                cmd.Parameters.AddWithValue("$anterior",contratoAnterior.ToString("D"));
                cmd.Parameters.AddWithValue("$principal",nuevo.Patente);
                cmd.ExecuteNonQuery();
            }
            AuditarSesion(db,tx,"Abono.Renovar",nuevo.Id.ToString("D"),
                $"Origen {contratoAnterior:D}; pendiente de pago");
            tx.Commit();
            return nuevo;
        }
        catch
        {
            // La creación pendiente no autoriza ingresos ni afecta caja.
            // Permitimos eliminar solo la solicitud huérfana si no hubo cobro.
            using var db=Abrir();using var tx=db.BeginTransaction();
            using(var d=db.CreateCommand())
            {
                d.Transaction=tx;
                d.CommandText="DELETE FROM AbonoVehiculos WHERE ContratoId=$id";
                d.Parameters.AddWithValue("$id",nuevo.Id.ToString("D"));
                d.ExecuteNonQuery();
            }
            using(var d=db.CreateCommand())
            {
                d.Transaction=tx;
                d.CommandText="DELETE FROM ContratosAbono WHERE Id=$id AND Estado='Pendiente'";
                d.Parameters.AddWithValue("$id",nuevo.Id.ToString("D"));
                d.ExecuteNonQuery();
            }
            tx.Commit();
            throw;
        }
    }
}
