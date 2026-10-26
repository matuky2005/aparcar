using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>
/// Persistencia MONOUSUARIO de demostración. No admite cajas en red.
/// </summary>
public sealed partial class AparcarDb
{
    private readonly string _connectionString;
    public string RutaDb { get; }

    public AparcarDb(string rutaDb)
    {
        RutaDb = Path.GetFullPath(rutaDb);
        Directory.CreateDirectory(Path.GetDirectoryName(RutaDb)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = RutaDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    private SqliteConnection Abrir()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return db;
    }

    public void Inicializar()
    {
        using var db = Abrir();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS EsquemaVersion(
                Numero INTEGER PRIMARY KEY, AplicadoUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Estadias(
                Ticket INTEGER PRIMARY KEY AUTOINCREMENT,
                Id TEXT NOT NULL UNIQUE,
                Patente TEXT NOT NULL,
                PatenteNormalizada TEXT NOT NULL,
                TipoVehiculo TEXT NOT NULL,
                Sector TEXT NOT NULL,
                IngresoUtc TEXT NOT NULL,
                SalidaUtc TEXT,
                Estado TEXT NOT NULL CHECK(Estado IN ('Activa','Finalizada'))
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Estadias_PatenteActiva
                ON Estadias(PatenteNormalizada) WHERE Estado = 'Activa';
            CREATE INDEX IF NOT EXISTS IX_Estadias_Ingreso ON Estadias(IngresoUtc);
            CREATE TABLE IF NOT EXISTS SalidasPrueba(
                Id TEXT PRIMARY KEY,
                EstadiaId TEXT NOT NULL UNIQUE REFERENCES Estadias(Id),
                ImporteSimuladoCentavos INTEGER NOT NULL
                    CHECK(ImporteSimuladoCentavos >= 0),
                FechaUtc TEXT NOT NULL
            );
            INSERT OR IGNORE INTO EsquemaVersion(Numero, AplicadoUtc)
                VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
        InicializarTickets();
        InicializarFinanzas();
        InicializarV4();
        InicializarSeguridad();
        InicializarModulos();
        InicializarV7();
        InicializarV8();
        InicializarV9();
        InicializarV10();
        InicializarV12Fiscal();
        InicializarV15Fiscal();
        InicializarV16Perfiles();
        InicializarV18Clientes();
        InicializarV19TicketsX();
        InicializarV21RecibosCobro();
    }

    public static string NormalizarPatente(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        return new string(texto.Trim().ToUpperInvariant()
            .Where(char.IsLetterOrDigit).ToArray());
    }

    public long RegistrarIngreso(string patente, string tipoVehiculo, string? sector, string? plaza = null)
    {
        ExigirPermiso(PermisosAparcar.Ingreso);
        var normalizada = NormalizarPatente(patente);
        if (normalizada.Length is < 3 or > 20)
            throw new ArgumentException("Ingresá una patente o identificador de 3 a 20 caracteres.");
        if (tipoVehiculo is not ("Auto" or "Moto" or "Camioneta"))
            throw new ArgumentException("Categoría de vehículo no válida.");
        var sectorFinal = string.IsNullOrWhiteSpace(sector) ? "General" : sector.Trim();
        if (sectorFinal.Length > 80) throw new ArgumentException("Sector demasiado largo.");

        using var db = Abrir();
        using var tx = db.BeginTransaction();
        var sectorValidado=VerificarSectorAntesIngreso(db,tx,sectorFinal);
        sectorFinal=sectorValidado.Nombre;
        var plazaElegida=ElegirPlaza(db,tx,sectorFinal,sectorValidado.Modo,plaza);
        var estadiaId = Guid.NewGuid().ToString("D");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO Estadias
                (Id,Patente,PatenteNormalizada,TipoVehiculo,Sector,IngresoUtc,Estado)
            VALUES($id,$patente,$normalizada,$tipo,$sector,$fecha,'Activa');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", estadiaId);
        cmd.Parameters.AddWithValue("$patente", normalizada);
        cmd.Parameters.AddWithValue("$normalizada", normalizada);
        cmd.Parameters.AddWithValue("$tipo", tipoVehiculo);
        cmd.Parameters.AddWithValue("$sector", sectorFinal);
        cmd.Parameters.AddWithValue("$fecha", DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            var ticket = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            GuardarToken(db, tx, estadiaId);
            AsignarTarifaAlIngresar(db, tx, estadiaId, tipoVehiculo);
            AsignarTarifaSectorAlIngresar(db,tx,estadiaId,sectorFinal,tipoVehiculo,DateTimeOffset.UtcNow);
            if(plazaElegida is not null)
                OcuparPlaza(db,tx,Guid.Parse(estadiaId),sectorFinal,plazaElegida);
            AuditarSesion(db,tx,"Vehiculo.Ingreso",estadiaId,
                $"Ticket {ticket} · {tipoVehiculo} · {sectorFinal}" +
                (plazaElegida is null?"":$" / plaza {plazaElegida}"));
            tx.Commit();
            return ticket;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "La patente ya tiene una estadía activa o el ingreso está duplicado.", ex);
        }
    }

    public List<Estadia> ListarActivas()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Ticket,Patente,TipoVehiculo,Sector,IngresoUtc,SalidaUtc,Estado
            FROM Estadias WHERE Estado='Activa' ORDER BY IngresoUtc DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var estadias = new List<Estadia>();
        while (reader.Read()) estadias.Add(Leer(reader));
        return estadias;
    }

    public Estadia? BuscarActiva(string ticketOPatente)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        var texto = ticketOPatente?.Trim() ?? "";
        if (texto.Length == 0) return null;
        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Ticket,Patente,TipoVehiculo,Sector,IngresoUtc,SalidaUtc,Estado
            FROM Estadias WHERE Estado='Activa'
              AND (PatenteNormalizada=$patente OR CAST(Ticket AS TEXT)=$ticket
                OR EXISTS(SELECT 1 FROM TicketsAcceso t
                    WHERE t.EstadiaId=Estadias.Id AND t.TokenQr=$token))
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$patente", NormalizarPatente(texto));
        cmd.Parameters.AddWithValue("$ticket", texto.TrimStart('0') is { Length: > 0 } n ? n : "0");
        cmd.Parameters.AddWithValue("$token", texto.StartsWith("APARCAR:", StringComparison.OrdinalIgnoreCase) ? texto["APARCAR:".Length..].Trim().ToUpperInvariant() : texto.ToUpperInvariant());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Leer(reader) : null;
    }

    /// <summary>
    /// Termina una estadía de prueba y conserva importe SIMULADO;
    /// no genera cobro real ni registra caja.
    /// </summary>
    public SalidaPrueba RegistrarSalidaPrueba(Guid estadiaId)
    {
        ExigirPermiso(PermisosAparcar.SalidaPrueba);
        using var db = Abrir();
        using var tx = db.BeginTransaction();
        Estadia? estadia;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Id,Ticket,Patente,TipoVehiculo,Sector,IngresoUtc,SalidaUtc,Estado
                FROM Estadias WHERE Id=$id AND Estado='Activa';
                """;
            cmd.Parameters.AddWithValue("$id", estadiaId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            estadia = reader.Read() ? Leer(reader) : null;
        }
        if (estadia is null)
            throw new InvalidOperationException("La estadía ya finalizó o no existe.");

        var ahora = DateTimeOffset.UtcNow;
        var importe = CalculadoraTarifas.CalcularCentavos(
            estadia.TipoVehiculo, ahora - estadia.IngresoUtc);

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE Estadias SET Estado='Finalizada', SalidaUtc=$fecha
                WHERE Id=$id AND Estado='Activa';
                """;
            cmd.Parameters.AddWithValue("$fecha", ahora.ToString("O"));
            cmd.Parameters.AddWithValue("$id", estadiaId.ToString("D"));
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("La estadía cambió durante la salida.");
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO SalidasPrueba(Id,EstadiaId,ImporteSimuladoCentavos,FechaUtc)
                VALUES($id,$estadia,$importe,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$estadia", estadiaId.ToString("D"));
            cmd.Parameters.AddWithValue("$importe", importe);
            cmd.Parameters.AddWithValue("$fecha", ahora.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        LiberarPlaza(db,tx,estadiaId);
        AuditarSesion(db,tx,"Vehiculo.SalidaPrueba",estadiaId.ToString("D"),$"Importe ficticio {importe} centavos");
        tx.Commit();
        return new SalidaPrueba(estadia.Ticket, estadia.Patente, importe, ahora);
    }

    public string CrearRespaldo()
    {
        ExigirPermiso(PermisosAparcar.Respaldo);
        return CrearRespaldoInterno();
    }

    private string CrearRespaldoInterno()
    {
        var carpeta = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments), "Aparcar", "Respaldos");
        Directory.CreateDirectory(carpeta);
        var ruta = Path.Combine(carpeta, $"aparcar_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db");
        using var origen = Abrir();
        using var destino = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ruta,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        destino.Open();
        origen.BackupDatabase(destino);
        return ruta;
    }

    private static Estadia Leer(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(7));
}
