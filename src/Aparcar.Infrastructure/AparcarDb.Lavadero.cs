using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarModulos()
    {
        using(var ver=Abrir())
        using(var cmd=ver.CreateCommand())
        {
            cmd.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=6";
            if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)==0)
                CrearRespaldoInterno(); // Antes del cambio, fuera del paquete MSIX.
        }
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS ServiciosLavado(
                Id TEXT PRIMARY KEY,
                Nombre TEXT NOT NULL,
                TipoVehiculo TEXT NOT NULL,
                Version INTEGER NOT NULL CHECK(Version>=1),
                PrecioCentavos INTEGER NOT NULL CHECK(PrecioCentavos>=0),
                Activo INTEGER NOT NULL CHECK(Activo IN(0,1)),
                CreadoUtc TEXT NOT NULL,
                UNIQUE(Nombre,TipoVehiculo,Version)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ServicioLavado_Vigente
                ON ServiciosLavado(Nombre,TipoVehiculo) WHERE Activo=1;
            CREATE TABLE IF NOT EXISTS OrdenesLavado(
                Numero INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                Patente TEXT NOT NULL,
                TipoVehiculo TEXT NOT NULL,
                ServicioId TEXT NOT NULL REFERENCES ServiciosLavado(Id),
                ServicioNombre TEXT NOT NULL,
                PrecioCentavos INTEGER NOT NULL CHECK(PrecioCentavos>=0),
                EstadiaId TEXT REFERENCES Estadias(Id),
                Estado TEXT NOT NULL CHECK(Estado IN
                    ('Pendiente','EnProceso','Terminado','Entregado','Cancelado')),
                EstadoPago TEXT NOT NULL CHECK(EstadoPago IN('Pendiente','Pagado')),
                Operario TEXT NOT NULL,
                Observaciones TEXT NOT NULL,
                CreadoUtc TEXT NOT NULL,
                ActualizadoUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Lavado_Estadia ON OrdenesLavado(EstadiaId,EstadoPago);
            CREATE INDEX IF NOT EXISTS IX_Lavado_Estado ON OrdenesLavado(Estado,CreadoUtc);
            CREATE TABLE IF NOT EXISTS EventosLavado(
                Id TEXT PRIMARY KEY,
                OrdenId TEXT NOT NULL REFERENCES OrdenesLavado(Id),
                Antes TEXT NOT NULL,
                Despues TEXT NOT NULL,
                Actor TEXT NOT NULL,
                FechaUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS PlanesAbono(
                Id TEXT PRIMARY KEY,
                Nombre TEXT NOT NULL,
                TipoVehiculo TEXT NOT NULL,
                Periodicidad TEXT NOT NULL CHECK(Periodicidad IN('Semanal','Quincenal','Mensual')),
                Version INTEGER NOT NULL CHECK(Version>=1),
                PrecioCentavos INTEGER NOT NULL CHECK(PrecioCentavos>=0),
                Activo INTEGER NOT NULL CHECK(Activo IN(0,1)),
                CreadoUtc TEXT NOT NULL,
                UNIQUE(Nombre,TipoVehiculo,Periodicidad,Version)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PlanAbono_Vigente
                ON PlanesAbono(Nombre,TipoVehiculo,Periodicidad) WHERE Activo=1;
            CREATE TABLE IF NOT EXISTS ContratosAbono(
                Id TEXT PRIMARY KEY,
                Cliente TEXT NOT NULL,
                Patente TEXT NOT NULL,
                PlanId TEXT NOT NULL REFERENCES PlanesAbono(Id),
                PlanNombre TEXT NOT NULL,
                TipoVehiculo TEXT NOT NULL,
                Periodicidad TEXT NOT NULL,
                PrecioCentavos INTEGER NOT NULL CHECK(PrecioCentavos>=0),
                InicioUtc TEXT,
                FinUtc TEXT,
                Estado TEXT NOT NULL CHECK(Estado IN('Pendiente','Activo','Cancelado')),
                ReferenciaCobro TEXT NOT NULL DEFAULT '',
                CreadoUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Abonos_Patente ON ContratosAbono(Patente,Estado,FinUtc);
            CREATE INDEX IF NOT EXISTS IX_Abonos_Estado ON ContratosAbono(Estado,FinUtc);
            CREATE TABLE IF NOT EXISTS CobrosComplementarios(
                Id TEXT PRIMARY KEY,
                ClaveOperacion TEXT NOT NULL UNIQUE,
                Categoria TEXT NOT NULL CHECK(Categoria IN('Lavadero','Abono')),
                ReferenciaId TEXT NOT NULL,
                TurnoCajaId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                MedioPago TEXT NOT NULL CHECK(MedioPago IN
                    ('Efectivo','Debito','Credito','Transferencia','QR')),
                ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos>=0),
                EntregadoCentavos INTEGER NOT NULL,
                VueltoCentavos INTEGER NOT NULL CHECK(VueltoCentavos>=0),
                ReferenciaExterna TEXT NOT NULL,
                Verificacion TEXT NOT NULL,
                Actor TEXT NOT NULL,
                FechaUtc TEXT NOT NULL,
                UNIQUE(Categoria,ReferenciaId)
            );
            CREATE INDEX IF NOT EXISTS IX_CobrosComplementarios_Turno
                ON CobrosComplementarios(TurnoCajaId,MedioPago);
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(6,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static bool TipoHabilitado(string tipo) => tipo is "Auto" or "Moto" or "Camioneta";
    private static string Fecha(DateTimeOffset fecha) => fecha.ToUniversalTime().ToString("O");
    private static DateTimeOffset ParsearFecha(string fecha) =>
        DateTimeOffset.Parse(fecha,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);

    public ServicioLavado GuardarServicioLavado(string nombre,string tipo,long precioCentavos)
    {
        ExigirPermiso(PermisosAparcar.LavaderoCatalogo);
        if(string.IsNullOrWhiteSpace(nombre)||nombre.Trim().Length is < 3 or > 80)
            throw new ArgumentException("El servicio debe tener nombre de 3 a 80 caracteres.");
        if(!TipoHabilitado(tipo))throw new ArgumentException("Tipo de vehículo no válido.");
        if(precioCentavos<0)throw new ArgumentOutOfRangeException(nameof(precioCentavos));
        var limpio=nombre.Trim();
        using var db=Abrir(); using var tx=db.BeginTransaction();
        int version;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT COALESCE(MAX(Version),0) FROM ServiciosLavado
                WHERE Nombre=$nombre AND TipoVehiculo=$tipo;
                """;
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            version=Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)+1;
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE ServiciosLavado SET Activo=0
                WHERE Nombre=$nombre AND TipoVehiculo=$tipo AND Activo=1;
                """;
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.ExecuteNonQuery();
        }
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO ServiciosLavado(Id,Nombre,TipoVehiculo,Version,PrecioCentavos,Activo,CreadoUtc)
                VALUES($id,$nombre,$tipo,$version,$precio,1,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$nombre",limpio);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$version",version);
            cmd.Parameters.AddWithValue("$precio",precioCentavos);
            cmd.Parameters.AddWithValue("$fecha",Fecha(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Lavadero.Servicio",id.ToString("D"),
            $"Nombre={limpio}; tipo={tipo}; precio={precioCentavos}; versión={version}");
        tx.Commit();
        return new ServicioLavado(id,limpio,tipo,version,precioCentavos,true);
    }

    public List<ServicioLavado> ListarServiciosLavado(bool soloActivos=true)
    {
        ExigirPermiso(PermisosAparcar.LavaderoConsultar);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,Nombre,TipoVehiculo,Version,PrecioCentavos,Activo
            FROM ServiciosLavado WHERE ($todos=1 OR Activo=1)
            ORDER BY TipoVehiculo,Nombre,Version DESC;
            """;
        cmd.Parameters.AddWithValue("$todos",soloActivos?0:1);
        using var r=cmd.ExecuteReader();
        var lista=new List<ServicioLavado>();
        while(r.Read())
            lista.Add(new ServicioLavado(Guid.Parse(r.GetString(0)),r.GetString(1),
                r.GetString(2),r.GetInt32(3),r.GetInt64(4),r.GetInt32(5)!=0));
        return lista;
    }

    public OrdenLavado CrearOrdenLavado(Guid servicioId,string patente,string? operario,
        string? observaciones,Guid? estadiaId=null)
    {
        ExigirPermiso(PermisosAparcar.LavaderoOperar);
        var normalizada=NormalizarPatente(patente);
        if(normalizada.Length is < 3 or > 20)
            throw new ArgumentException("La patente es obligatoria.");
        if((operario?.Length??0)>80 || (observaciones?.Length??0)>500)
            throw new ArgumentException("Texto de operario u observación demasiado largo.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        string nombre,tipo;long importe;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Nombre,TipoVehiculo,PrecioCentavos FROM ServiciosLavado
                WHERE Id=$id AND Activo=1;
                """;
            cmd.Parameters.AddWithValue("$id",servicioId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Servicio inexistente o dado de baja.");
            nombre=r.GetString(0);tipo=r.GetString(1);importe=r.GetInt64(2);
        }
        if(estadiaId.HasValue)
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT 1 FROM Estadias WHERE Id=$id AND Estado='Activa'
                   AND PatenteNormalizada=$patente AND TipoVehiculo=$tipo;
                """;
            cmd.Parameters.AddWithValue("$id",estadiaId.Value.ToString("D"));
            cmd.Parameters.AddWithValue("$patente",normalizada);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            if(cmd.ExecuteScalar() is null)
                throw new InvalidOperationException("La estadía no corresponde a esa patente/tipo o ya finalizó.");
        }
        var id=Guid.NewGuid();var ahora=DateTimeOffset.UtcNow;
        long numero;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO OrdenesLavado(Id,Patente,TipoVehiculo,ServicioId,
                    ServicioNombre,PrecioCentavos,EstadiaId,Estado,EstadoPago,
                    Operario,Observaciones,CreadoUtc,ActualizadoUtc)
                VALUES($id,$patente,$tipo,$servicio,$nombre,$precio,$estadia,'Pendiente',
                    'Pendiente',$operario,$obs,$fecha,$fecha);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$patente",normalizada);
            cmd.Parameters.AddWithValue("$tipo",tipo);
            cmd.Parameters.AddWithValue("$servicio",servicioId.ToString("D"));
            cmd.Parameters.AddWithValue("$nombre",nombre);
            cmd.Parameters.AddWithValue("$precio",importe);
            cmd.Parameters.AddWithValue("$estadia",estadiaId.HasValue
                ?estadiaId.Value.ToString("D"):(object)DBNull.Value);
            cmd.Parameters.AddWithValue("$operario",(operario??"").Trim());
            cmd.Parameters.AddWithValue("$obs",(observaciones??"").Trim());
            cmd.Parameters.AddWithValue("$fecha",Fecha(ahora));
            numero=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        AuditarSesion(db,tx,"Lavadero.OrdenCrear",id.ToString("D"),
            $"Orden #{numero}; {normalizada}; servicio={nombre}; importe={importe}");
        tx.Commit();
        return new OrdenLavado(id,numero,normalizada,tipo,servicioId,nombre,importe,
            estadiaId,EstadosLavado.Pendiente,"Pendiente",(operario??"").Trim(),
            (observaciones??"").Trim(),ahora);
    }

    private static OrdenLavado LeerOrden(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),r.GetInt64(1),r.GetString(2),r.GetString(3),
        Guid.Parse(r.GetString(4)),r.GetString(5),r.GetInt64(6),
        r.IsDBNull(7)?null:Guid.Parse(r.GetString(7)),
        r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),ParsearFecha(r.GetString(12)));

    private const string ColumnasOrden = """
        Id,Numero,Patente,TipoVehiculo,ServicioId,ServicioNombre,PrecioCentavos,
        EstadiaId,Estado,EstadoPago,Operario,Observaciones,CreadoUtc
        """;

    public List<OrdenLavado> ListarOrdenesLavado(int max=150)
    {
        ExigirPermiso(PermisosAparcar.LavaderoConsultar);
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasOrden} FROM OrdenesLavado ORDER BY Numero DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<OrdenLavado>();
        while(r.Read())lista.Add(LeerOrden(r));
        return lista;
    }

    public OrdenLavado CambiarEstadoOrdenLavado(Guid ordenId,string nuevoEstado,string? operario=null)
    {
        ExigirPermiso(PermisosAparcar.LavaderoOperar);
        if(!new[]{EstadosLavado.Pendiente,EstadosLavado.EnProceso,EstadosLavado.Terminado,
                   EstadosLavado.Entregado,EstadosLavado.Cancelado}.Contains(nuevoEstado))
            throw new ArgumentException("Estado inválido.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        OrdenLavado orden;
        using(var buscar=db.CreateCommand())
        {
            buscar.Transaction=tx;
            buscar.CommandText=$"SELECT {ColumnasOrden} FROM OrdenesLavado WHERE Id=$id";
            buscar.Parameters.AddWithValue("$id",ordenId.ToString("D"));
            using var r=buscar.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Orden no encontrada.");
            orden=LeerOrden(r);
        }
        if(!EstadosLavado.PuedeTransicionar(orden.Estado,nuevoEstado))
            throw new InvalidOperationException($"Transición no permitida: {orden.Estado} → {nuevoEstado}.");
        if(nuevoEstado==EstadosLavado.Cancelado && orden.EstadoPago=="Pagado")
            throw new InvalidOperationException("No se cancela una orden cobrada sin devolución contable.");
        var nuevoOperario=string.IsNullOrWhiteSpace(operario)?orden.Operario:operario.Trim();
        if(nuevoOperario.Length>80)throw new ArgumentException("Operario demasiado largo.");
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE OrdenesLavado SET Estado=$nuevo,Operario=$operario,ActualizadoUtc=$fecha
                WHERE Id=$id AND Estado=$anterior;
                """;
            cmd.Parameters.AddWithValue("$nuevo",nuevoEstado);
            cmd.Parameters.AddWithValue("$operario",nuevoOperario);
            cmd.Parameters.AddWithValue("$fecha",Fecha(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$id",ordenId.ToString("D"));
            cmd.Parameters.AddWithValue("$anterior",orden.Estado);
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Orden modificada por otro usuario.");
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO EventosLavado(Id,OrdenId,Antes,Despues,Actor,FechaUtc)
                VALUES($id,$orden,$antes,$despues,$actor,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$orden",ordenId.ToString("D"));
            cmd.Parameters.AddWithValue("$antes",orden.Estado);
            cmd.Parameters.AddWithValue("$despues",nuevoEstado);
            cmd.Parameters.AddWithValue("$actor",SesionActual?.Usuario??"Sistema");
            cmd.Parameters.AddWithValue("$fecha",Fecha(DateTimeOffset.UtcNow));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Lavadero.CambiarEstado",ordenId.ToString("D"),
            $"{orden.Estado} → {nuevoEstado}; operario={nuevoOperario}");
        tx.Commit();
        return orden with {Estado=nuevoEstado,Operario=nuevoOperario};
    }
}
