using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    public PlanAbono GuardarPlanAbono(string nombre,string tipo,string periodicidad,long precioCentavos)
    {
        ExigirPermiso(PermisosAparcar.AbonosAdministrar);
        if(string.IsNullOrWhiteSpace(nombre)||nombre.Trim().Length is < 3 or > 80)
            throw new ArgumentException("Nombre de plan inválido.");
        if(!TipoHabilitado(tipo))throw new ArgumentException("Tipo de vehículo inválido.");
        _=PeriodicidadesAbono.CalcularVencimiento(DateTimeOffset.UtcNow,periodicidad);
        if(precioCentavos<0)throw new ArgumentOutOfRangeException(nameof(precioCentavos));
        var limpio=nombre.Trim();
        using var db=Abrir();using var tx=db.BeginTransaction();
        int version;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COALESCE(MAX(Version),0) FROM PlanesAbono
                WHERE Nombre=$nombre AND TipoVehiculo=$tipo AND Periodicidad=$periodo;
                """;
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$periodo",periodicidad);
            version=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)+1;
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE PlanesAbono SET Activo=0
                WHERE Nombre=$nombre AND TipoVehiculo=$tipo AND Periodicidad=$periodo AND Activo=1;
                """;
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$periodo",periodicidad);
            cmd.ExecuteNonQuery();
        }
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO PlanesAbono(Id,Nombre,TipoVehiculo,Periodicidad,
                    Version,PrecioCentavos,Activo,CreadoUtc)
                VALUES($id,$nombre,$tipo,$periodo,$version,$precio,1,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$periodo",periodicidad);
            cmd.Parameters.AddWithValue("$version",version);
            cmd.Parameters.AddWithValue("$precio",precioCentavos);
            cmd.Parameters.AddWithValue("$fecha",Fecha(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Abono.PlanVersion",id.ToString("D"),
            $"Plan={limpio}; tipo={tipo}; período={periodicidad}; v={version}; precio={precioCentavos}");
        tx.Commit();
        return new PlanAbono(id,limpio,tipo,periodicidad,version,precioCentavos,true);
    }

    public List<PlanAbono> ListarPlanesAbono(bool activos=true)
    {
        ExigirPermiso(PermisosAparcar.AbonosConsultar);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,Nombre,TipoVehiculo,Periodicidad,Version,PrecioCentavos,Activo
            FROM PlanesAbono WHERE ($todos=1 OR Activo=1)
            ORDER BY Nombre,Version DESC;
            """;
        cmd.Parameters.AddWithValue("$todos",activos?0:1);
        using var r=cmd.ExecuteReader();
        var lista=new List<PlanAbono>();
        while(r.Read())lista.Add(new PlanAbono(Guid.Parse(r.GetString(0)),r.GetString(1),
            r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt64(5),r.GetInt32(6)!=0));
        return lista;
    }

    public ContratoAbono CrearContratoPendiente(string cliente,string patente,Guid planId)
    {
        ExigirPermiso(PermisosAparcar.AbonosAdministrar);
        if(string.IsNullOrWhiteSpace(cliente)||cliente.Trim().Length is < 3 or > 120)
            throw new ArgumentException("Nombre de cliente inválido.");
        var plate=NormalizarPatente(patente);
        if(plate.Length is < 3 or > 20)throw new ArgumentException("Patente inválida.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        string nombre,tipo,periodo;long precio;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Nombre,TipoVehiculo,Periodicidad,PrecioCentavos FROM PlanesAbono
                WHERE Id=$id AND Activo=1;
                """;
            cmd.Parameters.AddWithValue("$id",planId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("El plan no está vigente.");
            nombre=r.GetString(0);tipo=r.GetString(1);
            periodo=r.GetString(2);precio=r.GetInt64(3);
        }
        // No se crean contratos pendientes duplicados para un mismo plan/vehículo.
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COUNT(*) FROM ContratosAbono WHERE Patente=$patente
                AND PlanId=$plan AND Estado='Pendiente';
                """;
            cmd.Parameters.AddWithValue("$patente",plate);
            cmd.Parameters.AddWithValue("$plan",planId.ToString("D"));
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                throw new InvalidOperationException("Ya existe una solicitud pendiente para este vehículo y plan.");
        }
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO ContratosAbono(Id,Cliente,Patente,PlanId,PlanNombre,TipoVehiculo,
                    Periodicidad,PrecioCentavos,InicioUtc,FinUtc,Estado,ReferenciaCobro,CreadoUtc)
                VALUES($id,$cliente,$patente,$plan,$nombre,$tipo,$periodo,$precio,
                    NULL,NULL,'Pendiente','',$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$cliente",cliente.Trim());
            cmd.Parameters.AddWithValue("$patente",plate);
            cmd.Parameters.AddWithValue("$plan",planId.ToString("D"));
            cmd.Parameters.AddWithValue("$nombre",nombre);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$periodo",periodo);
            cmd.Parameters.AddWithValue("$precio",precio);
            cmd.Parameters.AddWithValue("$fecha",Fecha(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Abono.ContratoPendiente",id.ToString("D"),
            $"Cliente={cliente.Trim()}; patente={plate}; plan={nombre}");
        tx.Commit();
        return new ContratoAbono(id,cliente.Trim(),plate,planId,nombre,tipo,periodo,
            precio,null,null,"Pendiente","");
    }

    private const string ColumnasContrato="""
        Id,Cliente,Patente,PlanId,PlanNombre,TipoVehiculo,Periodicidad,
        PrecioCentavos,InicioUtc,FinUtc,Estado,ReferenciaCobro
        """;

    private static ContratoAbono LeerContrato(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),Guid.Parse(r.GetString(3)),
        r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt64(7),
        r.IsDBNull(8)?null:ParsearFecha(r.GetString(8)),
        r.IsDBNull(9)?null:ParsearFecha(r.GetString(9)),r.GetString(10),r.GetString(11));

    public List<ContratoAbono> ListarContratosAbono(int max=150)
    {
        ExigirPermiso(PermisosAparcar.AbonosConsultar);
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasContrato} FROM ContratosAbono ORDER BY CreadoUtc DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<ContratoAbono>();
        while(r.Read())lista.Add(LeerContrato(r));
        return lista;
    }

    private static bool CoberturaDisponible(SqliteConnection db,SqliteTransaction? tx,
        string patente,string tipo,DateTimeOffset desde,DateTimeOffset hasta)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT EXISTS(
              SELECT 1 FROM ContratosAbono WHERE Patente=$patente
              AND TipoVehiculo=$tipo AND Estado='Activo'
              AND InicioUtc<=$desde AND FinUtc>$hasta
            );
            """;
        cmd.Parameters.AddWithValue("$patente",patente);
        cmd.Parameters.AddWithValue("$tipo",tipo);
        cmd.Parameters.AddWithValue("$desde",Fecha(desde));
        cmd.Parameters.AddWithValue("$hasta",Fecha(hasta));
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)==1;
    }

    public CoberturaAbono? BuscarCobertura(string patente,string tipo,DateTimeOffset fecha)
    {
        ExigirPermiso(PermisosAparcar.AbonosConsultar);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,InicioUtc,FinUtc FROM ContratosAbono
            WHERE Patente=$patente AND TipoVehiculo=$tipo AND Estado='Activo'
              AND InicioUtc<=$fecha AND FinUtc>$fecha
            ORDER BY FinUtc DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$patente",NormalizarPatente(patente));
        cmd.Parameters.AddWithValue("$tipo",tipo);
        cmd.Parameters.AddWithValue("$fecha",Fecha(fecha));
        using var r=cmd.ExecuteReader();
        return r.Read()?new CoberturaAbono(Guid.Parse(r.GetString(0)),NormalizarPatente(patente),
            ParsearFecha(r.GetString(1)),ParsearFecha(r.GetString(2))):null;
    }
}
