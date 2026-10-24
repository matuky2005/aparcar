using System;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    /// <summary>
    /// Pagos de lavadero y abonos con su propio comprobante y clave idempotente.
    /// Efectivo declarado y pagos externos verificados manualmente.
    /// </summary>
    public CobroComplementario CobrarComplemento(string categoria,Guid referenciaId,
        string medio,long recibidoCentavos,string? referenciaExterna,bool comprobanteVerificado,
        Guid claveOperacion)
    {
        ExigirPermiso(PermisosAparcar.Cobro);
        if(categoria is not ("Lavadero" or "Abono"))
            throw new ArgumentException("Categoría desconocida.",nameof(categoria));
        if(!MediosPago.EsValido(medio))throw new ArgumentException("Medio de pago no válido.");
        if(referenciaId==Guid.Empty || claveOperacion==Guid.Empty)
            throw new ArgumentException("Identificadores de operación obligatorios.");
        if(recibidoCentavos<0)throw new ArgumentOutOfRangeException(nameof(recibidoCentavos));
        var efectivo=medio==MediosPago.Efectivo;
        var referencia=(referenciaExterna??"").Trim();
        if(!efectivo && (!comprobanteVerificado || referencia.Length is < 4 or > 200))
            throw new InvalidOperationException("El pago externo requiere referencia y verificación declarada.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        // Primero reintento idéntico: no se vuelve a registrar ningún ingreso.
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Id,Categoria,ReferenciaId,MedioPago,ImporteCentavos,
                    EntregadoCentavos,VueltoCentavos,ReferenciaExterna,FechaUtc
                FROM CobrosComplementarios WHERE ClaveOperacion=$clave;
                """;
            cmd.Parameters.AddWithValue("$clave",claveOperacion.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(r.Read())
            {
                var anterior=new CobroComplementario(Guid.Parse(r.GetString(0)),
                    r.GetString(1),Guid.Parse(r.GetString(2)),r.GetString(3),
                    r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),ParsearFecha(r.GetString(8)));
                if(anterior.Categoria!=categoria || anterior.ReferenciaId!=referenciaId ||
                   anterior.MedioPago!=medio || anterior.EntregadoCentavos!=recibidoCentavos ||
                   (!efectivo && r.GetString(7)!=referencia))
                    throw new InvalidOperationException("La clave ya fue usada para otra operación.");
                return anterior;
            }
        }
        var turno=LeerTurno(db,tx) ??
            throw new InvalidOperationException("Abrí un turno de caja antes de cobrar.");
        long importe;
        string descripcion,patente;
        string? periodicidad=null;
        string? renovacionDe=null;
        DateTimeOffset inicioCobertura=ahora;
        DateTimeOffset ahora=DateTimeOffset.UtcNow;
        if(categoria=="Lavadero")
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT PrecioCentavos,Estado,EstadoPago,Patente,ServicioNombre
                FROM OrdenesLavado WHERE Id=$id;
                """;
            cmd.Parameters.AddWithValue("$id",referenciaId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Orden de lavado inexistente.");
            importe=r.GetInt64(0);
            if(r.GetString(1)==EstadosLavado.Cancelado || r.GetString(2)=="Pagado")
                throw new InvalidOperationException("Orden cancelada o ya cobrada.");
            patente=r.GetString(3);descripcion=r.GetString(4);
        }
        else
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT PrecioCentavos,Estado,Patente,PlanNombre,Periodicidad,RenovacionDe
                FROM ContratosAbono WHERE Id=$id;
                """;
            cmd.Parameters.AddWithValue("$id",referenciaId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Contrato no encontrado.");
            importe=r.GetInt64(0);
            if(r.GetString(1)!="Pendiente")
                throw new InvalidOperationException("El contrato ya fue activado o cancelado.");
            patente=r.GetString(2);descripcion=r.GetString(3);periodicidad=r.GetString(4);
            renovacionDe=r.IsDBNull(5)?null:r.GetString(5);
        }
        if(!efectivo && recibidoCentavos!=importe)
            throw new InvalidOperationException("El importe electrónico tiene que ser exactamente el valor pendiente.");
        if(efectivo && recibidoCentavos<importe)
            throw new InvalidOperationException("El efectivo recibido es insuficiente.");
        var vuelto=efectivo?checked(recibidoCentavos-importe):0;

        if(categoria=="Abono")
        {
            if(renovacionDe is not null)
            {
                using var origen=db.CreateCommand();
                origen.Transaction=tx;
                origen.CommandText="""
                    SELECT FinUtc FROM ContratosAbono
                    WHERE Id=$id AND Estado='Activo' AND TipoVehiculo=
                        (SELECT TipoVehiculo FROM ContratosAbono WHERE Id=$nuevo);
                    """;
                origen.Parameters.AddWithValue("$id",renovacionDe);
                origen.Parameters.AddWithValue("$nuevo",referenciaId.ToString("D"));
                var previo=origen.ExecuteScalar() as string;
                if(previo is null)
                    throw new InvalidOperationException("La renovación perdió su contrato de origen.");
                var finAnterior=ParsearFecha(previo);
                if(finAnterior>inicioCobertura)inicioCobertura=finAnterior;
            }
            var finNueva=PeriodicidadesAbono.CalcularVencimiento(inicioCobertura,periodicidad!);
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COUNT(*) FROM ContratosAbono c
                JOIN AbonoVehiculos existentes ON existentes.ContratoId=c.Id
                JOIN AbonoVehiculos nuevos ON nuevos.ContratoId=$nuevo
                  AND nuevos.Patente=existentes.Patente
                  AND nuevos.TipoVehiculo=existentes.TipoVehiculo
                WHERE c.Estado='Activo' AND c.Id!=$nuevo
                  AND c.InicioUtc<$fin AND c.FinUtc>$inicio;
                """;
            cmd.Parameters.AddWithValue("$nuevo",referenciaId.ToString("D"));
            cmd.Parameters.AddWithValue("$inicio",Fecha(inicioCobertura));
            cmd.Parameters.AddWithValue("$fin",Fecha(finNueva));
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Algún vehículo ya tiene cobertura superpuesta con otro contrato.");
        }

        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO CobrosComplementarios(Id,ClaveOperacion,Categoria,ReferenciaId,
                    TurnoCajaId,MedioPago,ImporteCentavos,EntregadoCentavos,VueltoCentavos,
                    ReferenciaExterna,Verificacion,Actor,FechaUtc)
                VALUES($id,$clave,$cat,$ref,$turno,$medio,$importe,$recibido,$vuelto,
                    $referencia,$verificacion,$actor,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$clave",claveOperacion.ToString("D"));
            cmd.Parameters.AddWithValue("$cat",categoria);
            cmd.Parameters.AddWithValue("$ref",referenciaId.ToString("D"));
            cmd.Parameters.AddWithValue("$turno",turno.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$medio",medio);
            cmd.Parameters.AddWithValue("$importe",importe);
            cmd.Parameters.AddWithValue("$recibido",recibidoCentavos);
            cmd.Parameters.AddWithValue("$vuelto",vuelto);
            cmd.Parameters.AddWithValue("$referencia",efectivo?"":referencia);
            cmd.Parameters.AddWithValue("$verificacion",efectivo?"EfectivoDeclarado":"ExternoVerificadoManualmente");
            cmd.Parameters.AddWithValue("$actor",SesionActual?.Usuario??"");
            cmd.Parameters.AddWithValue("$fecha",Fecha(ahora));
            cmd.ExecuteNonQuery();
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            if(categoria=="Lavadero")
            {
                cmd.CommandText="""
                    UPDATE OrdenesLavado SET EstadoPago='Pagado',ActualizadoUtc=$ahora
                    WHERE Id=$id AND EstadoPago='Pendiente' AND Estado!='Cancelado';
                    """;
            }
            else
            {
                cmd.CommandText="""
                    UPDATE ContratosAbono SET Estado='Activo',InicioUtc=$inicio,
                        FinUtc=$fin,ReferenciaCobro=$cobro
                    WHERE Id=$id AND Estado='Pendiente';
                    """;
                cmd.Parameters.AddWithValue("$inicio",Fecha(inicioCobertura));
                cmd.Parameters.AddWithValue("$fin",Fecha(PeriodicidadesAbono.CalcularVencimiento(inicioCobertura,periodicidad!)));
                cmd.Parameters.AddWithValue("$cobro",id.ToString("D"));
            }
            cmd.Parameters.AddWithValue("$id",referenciaId.ToString("D"));
            cmd.Parameters.AddWithValue("$ahora",Fecha(ahora));
            if(cmd.ExecuteNonQuery()!=1)
                throw new InvalidOperationException("El estado cambió y el cobro fue revertido.");
        }
        AuditarSesion(db,tx,$"{categoria}.Cobro",referenciaId.ToString("D"),
            $"Patente={patente}; servicio={descripcion}; medio={medio}; importe={importe}");
        tx.Commit();
        return new CobroComplementario(id,categoria,referenciaId,medio,
            importe,recibidoCentavos,vuelto,ahora);
    }

    /// <summary>No cerrar estadía con servicios vinculados sin cobrar ni entregar.</summary>
    private static void VerificarLavadoAntesSalida(SqliteConnection db,
        SqliteTransaction? tx,Guid estadiaId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT COUNT(*) FROM OrdenesLavado
            WHERE EstadiaId=$id AND Estado!='Cancelado'
              AND (EstadoPago!='Pagado' OR Estado!='Entregado');
            """;
        cmd.Parameters.AddWithValue("$id",estadiaId.ToString("D"));
        if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
            throw new InvalidOperationException("Hay lavados pendientes de cobrar o entregar. Resolvelos en Lavadero antes de finalizar la salida.");
    }
    private List<LineaRecaudacion> LeerCobrosComplementarios(
        DateTimeOffset desdeUtc,DateTimeOffset hastaUtc)
    {
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT c.Categoria,COALESCE(l.Numero,0),
                COALESCE(l.Patente,a.Patente),c.MedioPago,c.ImporteCentavos,
                c.FechaUtc,c.Actor,0
            FROM CobrosComplementarios c
            LEFT JOIN OrdenesLavado l ON c.Categoria='Lavadero' AND l.Id=c.ReferenciaId
            LEFT JOIN ContratosAbono a ON c.Categoria='Abono' AND a.Id=c.ReferenciaId
            WHERE c.FechaUtc >= $desde AND c.FechaUtc < $hasta
            UNION ALL
            SELECT d.Categoria,COALESCE(l.Numero,0),
                COALESCE(l.Patente,a.Patente),'Efectivo',-d.ImporteCentavos,
                d.FechaUtc,d.AutorizadoPor,1
            FROM DevolucionesComplementarias d
            LEFT JOIN OrdenesLavado l ON d.Categoria='Lavadero' AND l.Id=d.ReferenciaId
            LEFT JOIN ContratosAbono a ON d.Categoria='Abono' AND a.Id=d.ReferenciaId
            WHERE d.FechaUtc >= $desde AND d.FechaUtc < $hasta;
            """;
        cmd.Parameters.AddWithValue("$desde",Fecha(desdeUtc));
        cmd.Parameters.AddWithValue("$hasta",Fecha(hastaUtc));
        using var r=cmd.ExecuteReader();
        var filas=new List<LineaRecaudacion>();
        while(r.Read())
            filas.Add(new LineaRecaudacion(r.GetInt64(1),r.GetString(2),
                r.GetString(3),r.GetInt64(4),ParsearFecha(r.GetString(5)),
                r.GetString(6),r.GetInt32(7)==1,r.GetString(0)));
        return filas;
    }

}
