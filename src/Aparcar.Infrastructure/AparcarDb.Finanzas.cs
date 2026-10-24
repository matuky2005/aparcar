using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>Reglas financieras locales v0.3: solo efectivo declarado por un operador.
/// Ninguna integración con pagos electrónicos ni documentación fiscal.</summary>
public sealed partial class AparcarDb
{
    private void InicializarFinanzas()
    {
        bool yaMigrado;
        using (var db = Abrir())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM EsquemaVersion WHERE Numero=3)";
            yaMigrado = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        // Respaldo coherente antes de tocar bases de versiones anteriores.
        if (!yaMigrado) CrearRespaldo();

        using var conexion = Abrir();
        using var tx = conexion.BeginTransaction();
        using (var cmd = conexion.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS TarifasVersionadas(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TipoVehiculo TEXT NOT NULL,
                    Version INTEGER NOT NULL,
                    PrimeraHoraCentavos INTEGER NOT NULL CHECK(PrimeraHoraCentavos >= 0),
                    FraccionCentavos INTEGER NOT NULL CHECK(FraccionCentavos >= 0),
                    MinutosFraccion INTEGER NOT NULL CHECK(MinutosFraccion BETWEEN 1 AND 1440),
                    Tope24HorasCentavos INTEGER NOT NULL CHECK(Tope24HorasCentavos >= PrimeraHoraCentavos),
                    CortesiaMinutos INTEGER NOT NULL CHECK(CortesiaMinutos BETWEEN 0 AND 60),
                    VigenteDesdeUtc TEXT NOT NULL,
                    UNIQUE(TipoVehiculo,Version)
                );
                CREATE TABLE IF NOT EXISTS EstadiaTarifa(
                    EstadiaId TEXT PRIMARY KEY REFERENCES Estadias(Id),
                    TarifaVersionId INTEGER NOT NULL REFERENCES TarifasVersionadas(Id)
                );
                CREATE TABLE IF NOT EXISTS TurnosCaja(
                    Id TEXT PRIMARY KEY,
                    Operador TEXT NOT NULL,
                    AperturaUtc TEXT NOT NULL,
                    FondoInicialCentavos INTEGER NOT NULL CHECK(FondoInicialCentavos >= 0),
                    CierreUtc TEXT,
                    EfectivoContadoCentavos INTEGER,
                    EfectivoEsperadoCentavos INTEGER,
                    DiferenciaCentavos INTEGER,
                    Estado TEXT NOT NULL CHECK(Estado IN ('Abierta','Cerrada'))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Caja_UnicaAbierta
                    ON TurnosCaja(Estado) WHERE Estado='Abierta';
                CREATE TABLE IF NOT EXISTS CobrosCaja(
                    Id TEXT PRIMARY KEY,
                    ClaveOperacion TEXT NOT NULL UNIQUE,
                    EstadiaId TEXT NOT NULL UNIQUE REFERENCES Estadias(Id),
                    TurnoCajaId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                    Ticket INTEGER NOT NULL,
                    ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos >= 0),
                    EntregadoCentavos INTEGER NOT NULL CHECK(EntregadoCentavos >= ImporteCentavos),
                    VueltoCentavos INTEGER NOT NULL CHECK(VueltoCentavos >= 0),
                    FechaUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_CobrosCaja_Turno ON CobrosCaja(TurnoCajaId);
                CREATE TABLE IF NOT EXISTS MovimientosEfectivo(
                    Id TEXT PRIMARY KEY,
                    CobroId TEXT NOT NULL UNIQUE REFERENCES CobrosCaja(Id),
                    TurnoCajaId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                    ImporteNetoCentavos INTEGER NOT NULL CHECK(ImporteNetoCentavos >= 0),
                    FechaUtc TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conexion.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM TarifasVersionadas";
            if (Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture) == 0)
            {
                foreach (var (tipo,primera,fraccion,tope) in new[]
                {
                    ("Auto",200000L,100000L,1200000L),
                    ("Moto",100000L,50000L,600000L),
                    ("Camioneta",300000L,150000L,1800000L)
                })
                {
                    using var insert = conexion.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = """
                        INSERT INTO TarifasVersionadas(TipoVehiculo,Version,
                            PrimeraHoraCentavos,FraccionCentavos,MinutosFraccion,
                            Tope24HorasCentavos,CortesiaMinutos,VigenteDesdeUtc)
                        VALUES($tipo,1,$primera,$fraccion,30,$tope,10,$fecha);
                        """;
                    insert.Parameters.AddWithValue("$tipo", tipo);
                    insert.Parameters.AddWithValue("$primera", primera);
                    insert.Parameters.AddWithValue("$fraccion", fraccion);
                    insert.Parameters.AddWithValue("$tope", tope);
                    insert.Parameters.AddWithValue("$fecha", DateTimeOffset.UtcNow.ToString("O"));
                    insert.ExecuteNonQuery();
                }
            }
        }

        // Vincular estadías antiguas a la primera tarifa; nunca recalcular los
        // importes simulados que dejaron versiones previas.
        using (var cmd = conexion.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO EstadiaTarifa(EstadiaId,TarifaVersionId)
                SELECT e.Id,t.Id FROM Estadias e JOIN TarifasVersionadas t
                   ON t.TipoVehiculo=e.TipoVehiculo AND t.Version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conexion.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static TarifaOperativa LeerTarifa(SqliteDataReader r) => new(
        r.GetInt64(0),r.GetString(1),r.GetInt32(2),r.GetInt64(3),
        r.GetInt64(4),r.GetInt32(5),r.GetInt64(6),r.GetInt32(7));

    private static string CamposTarifa =>
        "Id,TipoVehiculo,Version,PrimeraHoraCentavos,FraccionCentavos,MinutosFraccion,Tope24HorasCentavos,CortesiaMinutos";

    private static TarifaOperativa TarifaActual(
        SqliteConnection db, SqliteTransaction? tx, string tipo)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {CamposTarifa} FROM TarifasVersionadas WHERE TipoVehiculo=$tipo ORDER BY Version DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$tipo",tipo);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"No hay tarifa configurada para {tipo}.");
        return LeerTarifa(r);
    }

    public List<TarifaOperativa> ListarTarifas()
    {
        using var db = Abrir();
        var lista = new List<TarifaOperativa>();
        foreach (var tipo in new[] {"Auto","Moto","Camioneta"})
            lista.Add(TarifaActual(db,null,tipo));
        return lista;
    }

    public TarifaOperativa CambiarTarifa(string tipo,long primera,long fraccion,
        int minutosFraccion,long tope,int cortesia)
    {
        if (tipo is not ("Auto" or "Moto" or "Camioneta"))
            throw new ArgumentException("Tipo de vehículo inválido.",nameof(tipo));
        var temporal = new TarifaOperativa(0,tipo,0,primera,fraccion,minutosFraccion,tope,cortesia);
        MotorTarifas.Validar(temporal);
        using var db = Abrir();
        using var tx = db.BeginTransaction();
        var actual = TarifaActual(db,tx,tipo);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO TarifasVersionadas(TipoVehiculo,Version,PrimeraHoraCentavos,
                FraccionCentavos,MinutosFraccion,Tope24HorasCentavos,CortesiaMinutos,VigenteDesdeUtc)
            VALUES($tipo,$version,$primera,$fraccion,$minutos,$tope,$cortesia,$fecha);
            """;
        cmd.Parameters.AddWithValue("$tipo",tipo);
        cmd.Parameters.AddWithValue("$version",actual.Version + 1);
        cmd.Parameters.AddWithValue("$primera",primera);
        cmd.Parameters.AddWithValue("$fraccion",fraccion);
        cmd.Parameters.AddWithValue("$minutos",minutosFraccion);
        cmd.Parameters.AddWithValue("$tope",tope);
        cmd.Parameters.AddWithValue("$cortesia",cortesia);
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return TarifaActual(db,null,tipo);
    }

    private static void AsignarTarifaAlIngresar(SqliteConnection db,
        SqliteTransaction tx,string estadiaId,string tipo)
    {
        var tarifa=TarifaActual(db,tx,tipo);
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO EstadiaTarifa(EstadiaId,TarifaVersionId) VALUES($id,$tarifa)";
        cmd.Parameters.AddWithValue("$id",estadiaId);
        cmd.Parameters.AddWithValue("$tarifa",tarifa.Id);
        cmd.ExecuteNonQuery();
    }

    private static TarifaOperativa TarifaDeEstadia(SqliteConnection db,
        SqliteTransaction? tx,Guid estadiaId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=$"""
            SELECT {string.Join(",",CamposTarifa.Split(',').Select(x=>"t."+x))}
            FROM EstadiaTarifa et JOIN TarifasVersionadas t ON et.TarifaVersionId=t.Id
            WHERE et.EstadiaId=$id
            """;
        cmd.Parameters.AddWithValue("$id",estadiaId.ToString("D"));
        using var reader=cmd.ExecuteReader();
        if(!reader.Read())throw new InvalidOperationException("No hay tarifa asignada a la estadía.");
        return LeerTarifa(reader);
    }

    public TarifaOperativa ObtenerTarifaDeEstadia(Guid estadiaId)
    {
        using var db=Abrir();
        return TarifaDeEstadia(db,null,estadiaId);
    }

    public TurnoCajaOperativo? ObtenerTurnoAbierto()
    {
        using var db=Abrir();
        return LeerTurno(db,null);
    }

    private static TurnoCajaOperativo? LeerTurno(SqliteConnection db,SqliteTransaction? tx)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT c.Id,c.Operador,c.AperturaUtc,c.FondoInicialCentavos,
                COALESCE((SELECT SUM(p.ImporteNetoCentavos) FROM MovimientosEfectivo p WHERE p.TurnoCajaId=c.Id),0),
                COALESCE((SELECT SUM(m.ImporteCentavos) FROM MovimientosManuales m
                    WHERE m.TurnoId=c.Id AND m.Tipo='Deposito'),0),
                COALESCE((SELECT SUM(m.ImporteCentavos) FROM MovimientosManuales m
                    WHERE m.TurnoId=c.Id AND m.Tipo='Retiro'),0),
                COALESCE((SELECT SUM(d.ImporteCentavos) FROM DevolucionesCaja d
                    WHERE d.TurnoId=c.Id),0)
            FROM TurnosCaja c WHERE c.Estado='Abierta' LIMIT 1;
            """;
        using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        var fondo=r.GetInt64(3);
        var cobrado=r.GetInt64(4);
        var depositos=r.GetInt64(5);
        var retiros=r.GetInt64(6);
        var devoluciones=r.GetInt64(7);
        return new TurnoCajaOperativo(Guid.Parse(r.GetString(0)),r.GetString(1),
            DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            fondo,cobrado,checked(fondo+cobrado+depositos-retiros-devoluciones),true);
    }

    public TurnoCajaOperativo AbrirTurno(string operador,long fondoCentavos)
    {
        if(string.IsNullOrWhiteSpace(operador) || operador.Trim().Length > 80)
            throw new ArgumentException("Identificá al operador (1 a 80 caracteres).");
        if(fondoCentavos < 0)throw new ArgumentOutOfRangeException(nameof(fondoCentavos));
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        if(LeerTurno(db,tx) is not null)
            throw new InvalidOperationException("Ya existe un turno abierto.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO TurnosCaja(Id,Operador,AperturaUtc,FondoInicialCentavos,Estado)
            VALUES($id,$operador,$fecha,$fondo,'Abierta');
            """;
        cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$operador",operador.Trim());
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$fondo",fondoCentavos);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return LeerTurno(db,null)!;
    }

    public ArqueoCierre CerrarTurno(Guid turnoId,long contadoCentavos)
    {
        if(contadoCentavos < 0)throw new ArgumentOutOfRangeException(nameof(contadoCentavos));
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        var abierto=LeerTurno(db,tx);
        if(abierto is null || abierto.Id != turnoId)
            throw new InvalidOperationException("El turno ya está cerrado o no corresponde a esta caja.");
        var diferencia=checked(contadoCentavos-abierto.EsperadoCentavos);
        var ahora=DateTimeOffset.UtcNow;
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE TurnosCaja SET CierreUtc=$fecha,EfectivoContadoCentavos=$contado,
               EfectivoEsperadoCentavos=$esperado,DiferenciaCentavos=$diferencia,
               Estado='Cerrada' WHERE Id=$id AND Estado='Abierta';
            """;
        cmd.Parameters.AddWithValue("$fecha",ahora.ToString("O"));
        cmd.Parameters.AddWithValue("$contado",contadoCentavos);
        cmd.Parameters.AddWithValue("$esperado",abierto.EsperadoCentavos);
        cmd.Parameters.AddWithValue("$diferencia",diferencia);
        cmd.Parameters.AddWithValue("$id",turnoId.ToString("D"));
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("El turno cambió de estado.");
        tx.Commit();
        return new ArqueoCierre(turnoId,abierto.EsperadoCentavos,contadoCentavos,diferencia,ahora);
    }

    public CotizacionOperativa CotizarSalida(Guid estadiaId)
    {
        using var db=Abrir();
        return CotizarSalidaInterno(db,null,estadiaId,DateTimeOffset.UtcNow);
    }

    private static CotizacionOperativa CotizarSalidaInterno(
        SqliteConnection db,SqliteTransaction? tx,Guid estadiaId,DateTimeOffset ahora)
    {
        long ticket;
        string patente,tipo;
        DateTimeOffset inicio;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Ticket,Patente,TipoVehiculo,IngresoUtc FROM Estadias
                WHERE Id=$id AND Estado='Activa';
                """;
            cmd.Parameters.AddWithValue("$id",estadiaId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("La estadía no está activa.");
            ticket=r.GetInt64(0);
            patente=r.GetString(1);
            tipo=r.GetString(2);
            inicio=DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
        }
        var tarifa=TarifaDeEstadia(db,tx,estadiaId);
        var total=MotorTarifas.CalcularCentavos(tarifa,ahora-inicio);
        Guid? turno=LeerTurno(db,tx)?.Id;
        return new CotizacionOperativa(estadiaId,ticket,patente,tipo,ahora,
            ahora.AddMinutes(2),total,tarifa.Version,turno);
    }

    // Compatibilidad con el método v0.3: la lógica vive ahora en ConfirmarPago.
    public CobroEfectivoConfirmado ConfirmarEfectivo(
        CotizacionOperativa cotizacion,long entregadoCentavos,Guid claveOperacion)
    {
        var confirmado=ConfirmarPago(cotizacion,MediosPago.Efectivo,
            entregadoCentavos,"",false,claveOperacion);
        return new CobroEfectivoConfirmado(confirmado.Id,confirmado.EstadiaId,
            confirmado.Ticket,confirmado.ImporteCentavos,
            confirmado.EntregadoCentavos,confirmado.VueltoCentavos,confirmado.FechaUtc);
    }

    private static CobroEfectivoConfirmado LeerCobro(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetInt64(2),
        r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),
        DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));

    public List<CobroListado> ListarCobros(int max=100)
    {
        if(max is < 1 or > 1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT p.Ticket,e.Patente,p.ImporteCentavos,p.FechaUtc,c.Operador
            FROM CobrosCaja p JOIN Estadias e ON e.Id=p.EstadiaId
            JOIN TurnosCaja c ON c.Id=p.TurnoCajaId
            ORDER BY p.FechaUtc DESC LIMIT $limite;
            """;
        cmd.Parameters.AddWithValue("$limite",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<CobroListado>();
        while(r.Read())
            lista.Add(new CobroListado(r.GetInt64(0),r.GetString(1),r.GetInt64(2),
                DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
                r.GetString(4)));
        return lista;
    }
}
