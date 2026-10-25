using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

public sealed partial class AparcarDb
{
    private void InicializarV4()
    {
        bool existente;
        using (var db = Abrir())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM EsquemaVersion WHERE Numero=4)";
            existente = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        if (!existente) CrearRespaldoInterno();

        using var db4 = Abrir();
        using var tx = db4.BeginTransaction();
        using (var cmd = db4.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS CobroMedios(
                    CobroId TEXT PRIMARY KEY REFERENCES CobrosCaja(Id),
                    MedioPago TEXT NOT NULL CHECK(MedioPago IN
                        ('Efectivo','Debito','Credito','Transferencia','QR')),
                    Referencia TEXT NOT NULL,
                    Verificacion TEXT NOT NULL
                        CHECK(Verificacion IN ('EfectivoDeclarado','ComprobanteDeclarado')),
                    ConfirmadoPor TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS MovimientosManuales(
                    Id TEXT PRIMARY KEY,
                    TurnoId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                    Tipo TEXT NOT NULL CHECK(Tipo IN ('Deposito','Retiro')),
                    ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos > 0),
                    Motivo TEXT NOT NULL,
                    Operador TEXT NOT NULL,
                    FechaUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_MovimientosManuales_Turno ON MovimientosManuales(TurnoId);
                CREATE TABLE IF NOT EXISTS AutorizacionLocalCaja(
                    Clave TEXT PRIMARY KEY,
                    Salt BLOB NOT NULL,
                    Hash BLOB NOT NULL,
                    Iteraciones INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS DevolucionesCaja(
                    Id TEXT PRIMARY KEY,
                    CobroId TEXT NOT NULL UNIQUE REFERENCES CobrosCaja(Id),
                    TurnoId TEXT NOT NULL REFERENCES TurnosCaja(Id),
                    ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos >= 0),
                    Motivo TEXT NOT NULL,
                    AutorizadoPor TEXT NOT NULL,
                    FechaUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_DevolucionesCaja_Fecha ON DevolucionesCaja(FechaUtc);
                INSERT OR IGNORE INTO CobroMedios(CobroId,MedioPago,Referencia,Verificacion,ConfirmadoPor)
                SELECT p.Id,'Efectivo','Migrado desde v0.3','EfectivoDeclarado',c.Operador
                FROM CobrosCaja p JOIN TurnosCaja c ON c.Id=p.TurnoCajaId
                WHERE NOT EXISTS(SELECT 1 FROM CobroMedios m WHERE m.CobroId=p.Id);
                INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                    VALUES(4,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static long ScalarLong(SqliteConnection db, SqliteTransaction? tx, string sql, string? turno = null)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=sql;
        if (turno is not null) cmd.Parameters.AddWithValue("$turno",turno);
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
    }

    private static ResumenCajaV4? ResumenInterno(SqliteConnection db,SqliteTransaction? tx)
    {
        var turno=LeerTurno(db,tx);
        if(turno is null)return null;
        var id=turno.Id.ToString("D");
        long cash=ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(p.ImporteCentavos),0) FROM CobrosCaja p
            JOIN CobroMedios m ON m.CobroId=p.Id
            WHERE p.TurnoCajaId=$turno AND m.MedioPago='Efectivo'
            """,id) + ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM CobrosComplementarios
            WHERE TurnoCajaId=$turno AND MedioPago='Efectivo'
            """,id);
        long electronic=ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(p.ImporteCentavos),0) FROM CobrosCaja p
            JOIN CobroMedios m ON m.CobroId=p.Id
            WHERE p.TurnoCajaId=$turno AND m.MedioPago!='Efectivo'
            """,id) + ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM CobrosComplementarios
            WHERE TurnoCajaId=$turno AND MedioPago!='Efectivo'
            """,id);
        long deposits=ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM MovimientosManuales
            WHERE TurnoId=$turno AND Tipo='Deposito'
            """,id);
        long withdrawals=ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM MovimientosManuales
            WHERE TurnoId=$turno AND Tipo='Retiro'
            """,id);
        long refunds=ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM DevolucionesCaja
            WHERE TurnoId=$turno
            """,id) + ScalarLong(db,tx,"""
            SELECT COALESCE(SUM(ImporteCentavos),0) FROM DevolucionesComplementarias
            WHERE TurnoId=$turno
            """,id);
        return new ResumenCajaV4(turno.Id,turno.Operador,turno.FondoInicialCentavos,
            cash,electronic,deposits,withdrawals,refunds,
            checked(turno.FondoInicialCentavos+cash+deposits-withdrawals-refunds));
    }

    public ResumenCajaV4? ObtenerResumenCaja()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        var resumen=ResumenInterno(db,null);
        if(resumen is not null && OcultarImportesTurno(db,resumen.TurnoId))
            throw new UnauthorizedAccessException("Caja ciega: el saldo esperado se revela solo después de contar y cerrar el turno.");
        return resumen;
    }

    public MovimientoCajaManual RegistrarMovimientoManual(
        string tipo,long importeCentavos,string motivo,string operador)
    {
        ExigirPermiso(PermisosAparcar.MovimientosCaja);
        if(tipo is not ("Deposito" or "Retiro"))throw new ArgumentException("Tipo de movimiento inválido.");
        if(importeCentavos<=0)throw new ArgumentOutOfRangeException(nameof(importeCentavos));
        if(string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length<5 || motivo.Length>300)
            throw new ArgumentException("El motivo debe tener entre 5 y 300 caracteres.");
        if(string.IsNullOrWhiteSpace(operador) || operador.Trim().Length>80)
            throw new ArgumentException("Indicá el operador responsable.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        var resumen=ResumenInterno(db,tx) ?? throw new InvalidOperationException("No hay turno abierto.");
        if(tipo=="Retiro" && importeCentavos>resumen.EfectivoEsperadoCentavos)
            throw new InvalidOperationException("El retiro supera el efectivo esperado.");
        var id=Guid.NewGuid();
        var fecha=DateTimeOffset.UtcNow;
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO MovimientosManuales(Id,TurnoId,Tipo,ImporteCentavos,Motivo,Operador,FechaUtc)
            VALUES($id,$turno,$tipo,$importe,$motivo,$operador,$fecha);
            """;
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        cmd.Parameters.AddWithValue("$turno",resumen.TurnoId.ToString("D"));
        cmd.Parameters.AddWithValue("$tipo",tipo);
        cmd.Parameters.AddWithValue("$importe",importeCentavos);
        cmd.Parameters.AddWithValue("$motivo",motivo.Trim());
        cmd.Parameters.AddWithValue("$operador",operador.Trim());
        cmd.Parameters.AddWithValue("$fecha",fecha.ToString("O"));
        cmd.ExecuteNonQuery();
        AuditarSesion(db,tx,"Caja.Movimiento",id.ToString("D"),$"{tipo} {importeCentavos}; motivo {motivo.Trim()}");
        tx.Commit();
        return new MovimientoCajaManual(id,resumen.TurnoId,tipo,importeCentavos,motivo.Trim(),operador.Trim(),fecha);
    }

    public List<MovimientoCajaManual> ListarMovimientosManuales(int max=80)
    {
        ExigirPermiso(PermisosAparcar.Reportes);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,TurnoId,Tipo,ImporteCentavos,Motivo,Operador,FechaUtc
            FROM MovimientosManuales ORDER BY FechaUtc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var lista=new List<MovimientoCajaManual>();
        while(r.Read()) lista.Add(new MovimientoCajaManual(Guid.Parse(r.GetString(0)),
            Guid.Parse(r.GetString(1)),r.GetString(2),r.GetInt64(3),r.GetString(4),
            r.GetString(5),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind)));
        return lista;
    }

    /// <summary>
    /// Para electrónicos se exige referencia y una declaración EXPLÍCITA de verificación
    /// humana, que NO sustituye conciliación bancaria ni pasarela de pagos.
    /// </summary>
    public CobroConfirmadoV4 ConfirmarPago(
        CotizacionOperativa cotizacion,string medio,long entregadoCentavos,
        string referencia,bool comprobanteVerificado,Guid claveOperacion)
    {
        ExigirPermiso(PermisosAparcar.Cobro);
        if(!MediosPago.EsValido(medio))throw new ArgumentException("Medio de pago inválido.");
        if(claveOperacion==Guid.Empty)throw new ArgumentException("Se necesita una clave única.");
        var efectivo=medio==MediosPago.Efectivo;
        if(!efectivo && (!comprobanteVerificado || string.IsNullOrWhiteSpace(referencia) ||
            referencia.Trim().Length<4 || referencia.Length>200))
            throw new InvalidOperationException("Verificá el pago externo y registrá una referencia real.");
        if(entregadoCentavos<0)throw new ArgumentOutOfRangeException(nameof(entregadoCentavos));
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using(var buscar=db.CreateCommand())
        {
            buscar.Transaction=tx;
            buscar.CommandText="""
                SELECT p.Id,p.EstadiaId,p.Ticket,p.ImporteCentavos,p.EntregadoCentavos,
                       p.VueltoCentavos,p.FechaUtc,m.MedioPago,m.Referencia
                FROM CobrosCaja p JOIN CobroMedios m ON m.CobroId=p.Id
                WHERE p.ClaveOperacion=$clave;
                """;
            buscar.Parameters.AddWithValue("$clave",claveOperacion.ToString("D"));
            using var r=buscar.ExecuteReader();
            if(r.Read())
            {
                var previo=LeerCobroV4(r);
                if(previo.EstadiaId!=cotizacion.EstadiaId || previo.MedioPago!=medio ||
                   previo.EntregadoCentavos!=entregadoCentavos ||
                   (!efectivo && previo.Referencia!=referencia.Trim()))
                    throw new InvalidOperationException("Clave repetida con datos distintos.");
                return previo;
            }
        }
        var turno=LeerTurno(db,tx) ?? throw new InvalidOperationException("Abrí una caja antes de cobrar.");
        if(turno.Id!=cotizacion.TurnoId)throw new InvalidOperationException("El turno cambió. Volvé a cotizar.");
        var fecha=DateTimeOffset.UtcNow;
        if(fecha>cotizacion.VenceUtc)throw new InvalidOperationException("La cotización venció.");
        var vigente=CotizarSalidaInterno(db,tx,cotizacion.EstadiaId,fecha);
        VerificarLavadoAntesSalida(db,tx,cotizacion.EstadiaId);
        if(vigente.ImporteCentavos!=cotizacion.ImporteCentavos ||
           vigente.TarifaVersion!=cotizacion.TarifaVersion)
            throw new InvalidOperationException("El precio cambió. Volvé a cotizar.");

        // Pagos electrónicos: el total confirmado es exacto, nunca hay vuelto.
        if(efectivo && entregadoCentavos<vigente.ImporteCentavos)
            throw new InvalidOperationException("No alcanza el efectivo recibido.");
        if(!efectivo && entregadoCentavos!=vigente.ImporteCentavos)
            throw new InvalidOperationException("El importe electrónico debe coincidir exactamente.");
        long vuelto=efectivo ? checked(entregadoCentavos-vigente.ImporteCentavos) : 0;
        var cobroId=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO CobrosCaja(Id,ClaveOperacion,EstadiaId,TurnoCajaId,Ticket,
                    ImporteCentavos,EntregadoCentavos,VueltoCentavos,FechaUtc)
                VALUES($id,$clave,$estadia,$turno,$ticket,$importe,$entregado,$vuelto,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$clave",claveOperacion.ToString("D"));
            cmd.Parameters.AddWithValue("$estadia",cotizacion.EstadiaId.ToString("D"));
            cmd.Parameters.AddWithValue("$turno",turno.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$ticket",vigente.Ticket);
            cmd.Parameters.AddWithValue("$importe",vigente.ImporteCentavos);
            cmd.Parameters.AddWithValue("$entregado",entregadoCentavos);
            cmd.Parameters.AddWithValue("$vuelto",vuelto);
            cmd.Parameters.AddWithValue("$fecha",fecha.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO CobroMedios(CobroId,MedioPago,Referencia,Verificacion,ConfirmadoPor)
                VALUES($id,$medio,$referencia,$verificacion,$operador);
                """;
            cmd.Parameters.AddWithValue("$id",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$medio",medio);
            cmd.Parameters.AddWithValue("$referencia",efectivo ? "" : referencia.Trim());
            cmd.Parameters.AddWithValue("$verificacion",efectivo?"EfectivoDeclarado":"ComprobanteDeclarado");
            cmd.Parameters.AddWithValue("$operador",turno.Operador);
            cmd.ExecuteNonQuery();
        }
        if(efectivo)
        {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO MovimientosEfectivo(Id,CobroId,TurnoCajaId,ImporteNetoCentavos,FechaUtc)
                VALUES($id,$cobro,$turno,$importe,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$turno",turno.Id.ToString("D"));
            cmd.Parameters.AddWithValue("$importe",vigente.ImporteCentavos);
            cmd.Parameters.AddWithValue("$fecha",fecha.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE Estadias SET Estado='Finalizada',SalidaUtc=$fecha
                WHERE Id=$id AND Estado='Activa';
                """;
            cmd.Parameters.AddWithValue("$fecha",fecha.ToString("O"));
            cmd.Parameters.AddWithValue("$id",cotizacion.EstadiaId.ToString("D"));
            if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("La estadía ya estaba cerrada.");
        }
        LiberarPlaza(db,tx,cotizacion.EstadiaId);
        RegistrarPendienteFiscal(db,tx,"Estacionamiento",cobroId,
            vigente.ImporteCentavos,fecha);
        AuditarSesion(db,tx,"Caja.Cobro",cobroId.ToString("D"),$"Medio={medio}; importe={vigente.ImporteCentavos}; ticket={vigente.Ticket}");
        tx.Commit();
        return new CobroConfirmadoV4(cobroId,cotizacion.EstadiaId,vigente.Ticket,medio,
            vigente.ImporteCentavos,entregadoCentavos,vuelto,efectivo?"":referencia.Trim(),fecha);
    }

    private static CobroConfirmadoV4 LeerCobroV4(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetInt64(2),
        r.GetString(7),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),
        r.GetString(8),DateTimeOffset.Parse(r.GetString(6),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));

    public bool ExistePinSupervisor()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        return ScalarLong(db,null,"SELECT COUNT(*) FROM AutorizacionLocalCaja WHERE Clave='Supervisor'")>0;
    }

    public void ConfigurarPinSupervisorInicial(string pin)
    {
        ExigirPermiso(PermisosAparcar.Usuarios);
        if(pin is null || pin.Length<6 || pin.Length>12 || !pin.All(char.IsDigit))
            throw new ArgumentException("El PIN debe tener de 6 a 12 dígitos.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        byte[] salt=RandomNumberGenerator.GetBytes(24);
        const int iteraciones=210_000;
        byte[] hash=Rfc2898DeriveBytes.Pbkdf2(pin,salt,iteraciones,HashAlgorithmName.SHA256,32);
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO AutorizacionLocalCaja(Clave,Salt,Hash,Iteraciones)
            VALUES('Supervisor',$salt,$hash,$iteraciones);
            """;
        cmd.Parameters.AddWithValue("$salt",salt);
        cmd.Parameters.AddWithValue("$hash",hash);
        cmd.Parameters.AddWithValue("$iteraciones",iteraciones);
        try { cmd.ExecuteNonQuery(); AuditarSesion(db,tx,"Supervisor.PinInicial","Caja","PIN configurado"); tx.Commit(); }
        catch(SqliteException ex) when(ex.SqliteErrorCode==19)
        { throw new InvalidOperationException("El PIN ya está configurado.",ex); }
    }

    private static bool VerificarPin(SqliteConnection db,SqliteTransaction tx,string pin)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="SELECT Salt,Hash,Iteraciones FROM AutorizacionLocalCaja WHERE Clave='Supervisor'";
        using var r=cmd.ExecuteReader();
        if(!r.Read())throw new InvalidOperationException("Configurá antes un PIN local de supervisor.");
        var salt=(byte[])r.GetValue(0);
        var esperado=(byte[])r.GetValue(1);
        int iteraciones=r.GetInt32(2);
        byte[] calculado=Rfc2898DeriveBytes.Pbkdf2(pin,salt,iteraciones,HashAlgorithmName.SHA256,esperado.Length);
        return CryptographicOperations.FixedTimeEquals(calculado,esperado);
    }

    /// <summary>Solo devolución COMPLETA de cobros en EFECTIVO, con PIN local.
    /// No afirma que se haya realizado reintegro en tarjeta o banco.</summary>
    public DevolucionEfectivo DevolverCobroEfectivo(
        Guid cobroId,string motivo,string autorizador,string pin)
    {
        ExigirPermiso(PermisosAparcar.Devolucion);
        if(string.IsNullOrWhiteSpace(motivo) || motivo.Trim().Length<8 || motivo.Length>300)
            throw new ArgumentException("Explicá el motivo (mínimo 8 caracteres).");
        if(string.IsNullOrWhiteSpace(autorizador) || autorizador.Trim().Length>80)
            throw new ArgumentException("Identificá quién autorizó la devolución.");
        if(string.IsNullOrWhiteSpace(pin))throw new InvalidOperationException("Ingresá el PIN de supervisor.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        if(!VerificarPin(db,tx,pin))throw new InvalidOperationException("PIN de supervisor incorrecto.");
        var turno=ResumenInterno(db,tx) ?? throw new InvalidOperationException("No hay caja abierta.");
        long importe,ticket;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT p.ImporteCentavos,p.Ticket FROM CobrosCaja p
                JOIN CobroMedios m ON m.CobroId=p.Id
                WHERE p.Id=$id AND m.MedioPago='Efectivo'
                  AND NOT EXISTS(SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.Id);
                """;
            cmd.Parameters.AddWithValue("$id",cobroId.ToString("D"));
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new InvalidOperationException("Cobro inexistente, electrónico o ya devuelto.");
            importe=r.GetInt64(0);
            ticket=r.GetInt64(1);
        }
        if(importe>turno.EfectivoEsperadoCentavos)
            throw new InvalidOperationException("El efectivo esperado no alcanza para devolver.");
        var id=Guid.NewGuid();var ahora=DateTimeOffset.UtcNow;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO DevolucionesCaja(Id,CobroId,TurnoId,ImporteCentavos,Motivo,AutorizadoPor,FechaUtc)
                VALUES($id,$cobro,$turno,$importe,$motivo,$autorizador,$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            cmd.Parameters.AddWithValue("$turno",turno.TurnoId.ToString("D"));
            cmd.Parameters.AddWithValue("$importe",importe);
            cmd.Parameters.AddWithValue("$motivo",motivo.Trim());
            cmd.Parameters.AddWithValue("$autorizador",autorizador.Trim());
            cmd.Parameters.AddWithValue("$fecha",ahora.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"Caja.Devolucion",cobroId.ToString("D"),$"Importe={importe}; autorizador={autorizador.Trim()}; motivo={motivo.Trim()}");
        tx.Commit();
        return new DevolucionEfectivo(id,cobroId,turno.TurnoId,ticket,importe,motivo.Trim(),autorizador.Trim(),ahora);
    }

    public List<(Guid Id,long Ticket,string Patente,long ImporteCentavos,bool Devuelto)> CobrosEfectivoParaDevolver(int max=100)
    {
        ExigirPermiso(PermisosAparcar.Devolucion);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT p.Id,p.Ticket,e.Patente,p.ImporteCentavos,
                   EXISTS(SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.Id)
            FROM CobrosCaja p JOIN Estadias e ON e.Id=p.EstadiaId
            JOIN CobroMedios m ON m.CobroId=p.Id
            WHERE m.MedioPago='Efectivo' ORDER BY p.FechaUtc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var rows=new List<(Guid,long,string,long,bool)>();
        while(r.Read())rows.Add((Guid.Parse(r.GetString(0)),r.GetInt64(1),
            r.GetString(2),r.GetInt64(3),r.GetInt64(4)!=0));
        return rows;
    }

    public ResumenRecaudacion ReporteRecaudacion(DateTimeOffset desdeUtc,DateTimeOffset hastaUtc)
    {
        ExigirPermiso(PermisosAparcar.Reportes);
        if(hastaUtc<=desdeUtc)throw new ArgumentException("El período final debe ser posterior al inicial.");
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT p.Ticket,e.Patente,m.MedioPago,p.ImporteCentavos,p.FechaUtc,c.Operador,0
            FROM CobrosCaja p JOIN Estadias e ON e.Id=p.EstadiaId
            JOIN CobroMedios m ON m.CobroId=p.Id
            JOIN TurnosCaja c ON c.Id=p.TurnoCajaId
            WHERE p.FechaUtc >= $desde AND p.FechaUtc < $hasta
            UNION ALL
            SELECT p.Ticket,e.Patente,'DevolucionEfectivo',-d.ImporteCentavos,d.FechaUtc,d.AutorizadoPor,1
            FROM DevolucionesCaja d JOIN CobrosCaja p ON p.Id=d.CobroId
            JOIN Estadias e ON e.Id=p.EstadiaId
            WHERE d.FechaUtc >= $desde AND d.FechaUtc < $hasta
            ORDER BY FechaUtc DESC;
            """;
        cmd.Parameters.AddWithValue("$desde",desdeUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$hasta",hastaUtc.ToUniversalTime().ToString("O"));
        using var r=cmd.ExecuteReader();
        var rows=new List<LineaRecaudacion>();
        while(r.Read())rows.Add(new LineaRecaudacion(
            r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3),
            DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            r.GetString(5),r.GetInt32(6)!=0));
        rows.AddRange(LeerCobrosComplementarios(desdeUtc,hastaUtc));
        rows=rows.OrderByDescending(x=>x.FechaUtc).ToList();
        long bruto=rows.Where(x=>!x.Devuelto).Sum(x=>x.ImporteCentavos);
        long devoluciones=-rows.Where(x=>x.Devuelto).Sum(x=>x.ImporteCentavos);
        long efectivo=rows.Where(x=>x.MedioPago=="Efectivo" || x.Devuelto).Sum(x=>x.ImporteCentavos);
        return new ResumenRecaudacion(bruto,devoluciones,bruto-devoluciones,
            efectivo,rows.Where(x=>!x.Devuelto && x.MedioPago!="Efectivo").Sum(x=>x.ImporteCentavos),rows);
    }
}
