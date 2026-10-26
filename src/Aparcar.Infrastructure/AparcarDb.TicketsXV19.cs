using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;
using Microsoft.Data.Sqlite;

namespace Aparcar.Infrastructure;

/// <summary>Numeración propia, nunca equivalente a un punto de venta ARCA.</summary>
public sealed record TalonarioX(string Codigo,string Nombre,long UltimoNumero,bool Habilitado);
public sealed record CobroDisponibleX(string Origen,Guid CobroId,long ImporteCentavos,
    string MedioPago,string Descripcion,DateTimeOffset FechaCobroUtc,bool YaEmitido);
public sealed record TicketX(Guid Id,string Talonario,long Numero,string Origen,Guid CobroId,
    long ImporteCentavos,string MedioPago,string Descripcion,DateTimeOffset FechaCobroUtc,
    DateTimeOffset EmitidoUtc,bool Devuelto);
public sealed record ResultadoEmisionX(TicketX Ticket,bool EsNuevo);

/// <summary>
/// Talonarios X no fiscales. Solo documentan operaciones cobradas previamente.
/// No crean cobros, no emiten CAE, no tocan PendientesFiscales y no autorizan ventas.
/// </summary>
public sealed partial class AparcarDb
{
    private void InicializarV19TicketsX()
    {
        using(var cn=Abrir())
        using(var consulta=cn.CreateCommand())
        {
            consulta.CommandText="SELECT COUNT(*) FROM EsquemaVersion WHERE Numero=16";
            if(Convert.ToInt64(consulta.ExecuteScalar(),CultureInfo.InvariantCulture)>0)return;
        }
        CrearRespaldoInterno();
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS TalonariosX(
                Codigo TEXT PRIMARY KEY NOT NULL,
                Nombre TEXT NOT NULL,
                UltimoNumero INTEGER NOT NULL DEFAULT 0 CHECK(UltimoNumero >= 0),
                Habilitado INTEGER NOT NULL DEFAULT 1 CHECK(Habilitado IN (0,1))
            );
            CREATE TABLE IF NOT EXISTS TicketsX(
                Id TEXT PRIMARY KEY,
                Talonario TEXT NOT NULL REFERENCES TalonariosX(Codigo),
                Numero INTEGER NOT NULL CHECK(Numero>0),
                Origen TEXT NOT NULL CHECK(Origen IN('Estacionamiento','Lavadero','Abono')),
                CobroId TEXT NOT NULL,
                ImporteCentavos INTEGER NOT NULL CHECK(ImporteCentavos>0),
                MedioPago TEXT NOT NULL,
                Descripcion TEXT NOT NULL,
                FechaCobroUtc TEXT NOT NULL,
                EmitidoUtc TEXT NOT NULL,
                UNIQUE(Talonario,Numero),
                UNIQUE(Origen,CobroId)
            );
            CREATE INDEX IF NOT EXISTS IX_TicketsX_Emision ON TicketsX(EmitidoUtc);
            CREATE TABLE IF NOT EXISTS ImpresionesX(
                Id TEXT PRIMARY KEY,
                TicketXId TEXT NOT NULL REFERENCES TicketsX(Id),
                EsCopia INTEGER NOT NULL CHECK(EsCopia IN(0,1)),
                Motivo TEXT NOT NULL,
                Impresora TEXT NOT NULL,
                Estado TEXT NOT NULL CHECK(Estado IN('Pendiente','Enviado','Error')),
                Detalle TEXT NOT NULL,
                CreadaUtc TEXT NOT NULL,
                TerminadaUtc TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_ImpresionesX_Ticket ON ImpresionesX(TicketXId,CreadaUtc);
            INSERT OR IGNORE INTO EsquemaVersion(Numero,AplicadoUtc)
                VALUES(16,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static string NormalizarCodigoX(string codigo)
    {
        codigo=(codigo??"").Trim();
        if(codigo.Length!=4 || !int.TryParse(codigo,NumberStyles.None,
             CultureInfo.InvariantCulture,out int nro) || nro<1)
            throw new ArgumentException("El talonario X debe tener cuatro dígitos: 0001 a 9999.");
        return codigo;
    }

    public TalonarioX CrearTalonarioX(string codigo,string nombre)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        codigo=NormalizarCodigoX(codigo);
        nombre=(nombre??"").Trim();
        if(nombre.Length is <3 or >80 || System.Linq.Enumerable.Any(nombre,char.IsControl))
            throw new ArgumentException("Nombre de talonario inválido.");
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO TalonariosX(Codigo,Nombre,UltimoNumero,Habilitado)
            VALUES($codigo,$nombre,0,1);
            """;
        cmd.Parameters.AddWithValue("$codigo",codigo);
        cmd.Parameters.AddWithValue("$nombre",nombre);
        try {cmd.ExecuteNonQuery();}
        catch(SqliteException ex) when(ex.SqliteErrorCode==19)
        {
            throw new InvalidOperationException("Ese código ya existe. No se reinició la numeración.",ex);
        }
        AuditarSesion(db,tx,"TicketX.Talonario",codigo,"Alta de talonario X no fiscal.");
        tx.Commit();
        return new TalonarioX(codigo,nombre,0,true);
    }

    public IReadOnlyList<TalonarioX> ListarTalonariosX()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Codigo,Nombre,UltimoNumero,Habilitado FROM TalonariosX ORDER BY Codigo";
        using var r=cmd.ExecuteReader();
        var lista=new List<TalonarioX>();
        while(r.Read())lista.Add(new TalonarioX(r.GetString(0),r.GetString(1),
            r.GetInt64(2),r.GetInt32(3)==1));
        return lista;
    }

    /// <summary>
    /// Talonario predeterminado de la operación diaria (no es un punto de venta ARCA).
    /// La elección de imprimir X se hace igualmente en cada cobro.
    /// </summary>
    public TalonarioX? ObtenerTalonarioXParaCobros()
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT t.Codigo,t.Nombre,t.UltimoNumero,t.Habilitado
            FROM PreferenciasDispositivos p
            JOIN TalonariosX t ON t.Codigo=p.Valor
            WHERE p.Clave='TicketX.TalonarioPredeterminado';
            """;
        using var r=cmd.ExecuteReader();
        return r.Read()?new TalonarioX(r.GetString(0),r.GetString(1),
            r.GetInt64(2),r.GetInt32(3)==1):null;
    }

    public TalonarioX ConfigurarTalonarioXParaCobros(string codigo)
    {
        ExigirPermiso(PermisosAparcar.Configuracion);
        codigo=NormalizarCodigoX(codigo);
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        TalonarioX talonario;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Nombre,UltimoNumero,Habilitado
                FROM TalonariosX WHERE Codigo=$codigo;
                """;
            cmd.Parameters.AddWithValue("$codigo",codigo);
            using var r=cmd.ExecuteReader();
            if(!r.Read() || r.GetInt32(2)!=1)
                throw new InvalidOperationException(
                    "El talonario X no existe o no está habilitado.");
            talonario=new TalonarioX(codigo,r.GetString(0),r.GetInt64(1),true);
        }
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO PreferenciasDispositivos(Clave,Valor)
                VALUES('TicketX.TalonarioPredeterminado',$codigo)
                ON CONFLICT(Clave) DO UPDATE SET Valor=excluded.Valor;
                """;
            cmd.Parameters.AddWithValue("$codigo",codigo);
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"TicketX.TalonarioCobros",codigo,
            "Talonario seleccionado para la emisión X opcional al finalizar cobros.");
        tx.Commit();
        return talonario;
    }

    private static (long Importe,string Medio,string Descripcion,DateTimeOffset Fecha,bool Devuelto)?
        ObtenerCobroX(SqliteConnection db,SqliteTransaction? tx,string origen,Guid id)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=origen switch
        {
            "Estacionamiento" => """
                SELECT p.ImporteCentavos,m.MedioPago,
                    'Estacionamiento - Ticket ' || CAST(p.Ticket AS TEXT) ||
                    ' - ' || e.Patente,p.FechaUtc,
                    EXISTS(SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.Id)
                FROM CobrosCaja p
                JOIN CobroMedios m ON m.CobroId=p.Id
                JOIN Estadias e ON e.Id=p.EstadiaId
                WHERE p.Id=$id
                """,
            "Lavadero" => """
                SELECT c.ImporteCentavos,c.MedioPago,
                    'Lavadero - ' || l.ServicioNombre || ' - ' || l.Patente,
                    c.FechaUtc,
                    EXISTS(SELECT 1 FROM DevolucionesComplementarias d WHERE d.CobroId=c.Id)
                FROM CobrosComplementarios c
                JOIN OrdenesLavado l ON l.Id=c.ReferenciaId
                WHERE c.Id=$id AND c.Categoria='Lavadero'
                """,
            "Abono" => """
                SELECT c.ImporteCentavos,c.MedioPago,
                    'Abono - ' || a.PlanNombre || ' - ' || a.Patente,
                    c.FechaUtc,
                    EXISTS(SELECT 1 FROM DevolucionesComplementarias d WHERE d.CobroId=c.Id)
                FROM CobrosComplementarios c
                JOIN ContratosAbono a ON a.Id=c.ReferenciaId
                WHERE c.Id=$id AND c.Categoria='Abono'
                """,
            _=>throw new ArgumentException("Origen de cobro inválido.")
        };
        cmd.Parameters.AddWithValue("$id",id.ToString("D"));
        using var r=cmd.ExecuteReader();
        if(!r.Read())return null;
        return(r.GetInt64(0),r.GetString(1),r.GetString(2),
            DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),r.GetInt64(4)!=0);
    }

    public IReadOnlyList<CobroDisponibleX> ListarCobrosParaX(int max=150)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Origen,CobroId,ImporteCentavos,MedioPago,Descripcion,FechaCobroUtc,
                EXISTS(SELECT 1 FROM TicketsX t WHERE t.Origen=origenes.Origen AND
                  t.CobroId=origenes.CobroId)
            FROM (
              SELECT 'Estacionamiento' AS Origen,p.Id AS CobroId,p.ImporteCentavos,
                m.MedioPago,'Estacionamiento - Ticket ' || p.Ticket || ' - ' || e.Patente
                    AS Descripcion,p.FechaUtc AS FechaCobroUtc
              FROM CobrosCaja p JOIN CobroMedios m ON m.CobroId=p.Id
              JOIN Estadias e ON e.Id=p.EstadiaId
              WHERE p.ImporteCentavos>0 AND NOT EXISTS
                  (SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.Id)
              UNION ALL
              SELECT c.Categoria,c.Id,c.ImporteCentavos,c.MedioPago,
                c.Categoria || ' - ' || COALESCE(l.ServicioNombre,a.PlanNombre,'Servicio'),
                c.FechaUtc
              FROM CobrosComplementarios c
              LEFT JOIN OrdenesLavado l ON c.Categoria='Lavadero' AND l.Id=c.ReferenciaId
              LEFT JOIN ContratosAbono a ON c.Categoria='Abono' AND a.Id=c.ReferenciaId
              WHERE c.ImporteCentavos>0 AND NOT EXISTS
                  (SELECT 1 FROM DevolucionesComplementarias d WHERE d.CobroId=c.Id)
            ) origenes ORDER BY FechaCobroUtc DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        var lista=new List<CobroDisponibleX>();
        using var r=cmd.ExecuteReader();
        while(r.Read())lista.Add(new CobroDisponibleX(r.GetString(0),
            Guid.Parse(r.GetString(1)),r.GetInt64(2),r.GetString(3),
            r.GetString(4),DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),r.GetInt64(6)!=0));
        return lista;
    }

    private static TicketX LeerTicketX(SqliteDataReader r,bool devuelto)=>
        new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetInt64(2),
            r.GetString(3),Guid.Parse(r.GetString(4)),r.GetInt64(5),
            r.GetString(6),r.GetString(7),
            DateTimeOffset.Parse(r.GetString(8),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(r.GetString(9),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),
            devuelto);

    private const string ColumnasX=
        "Id,Talonario,Numero,Origen,CobroId,ImporteCentavos,MedioPago,Descripcion,FechaCobroUtc,EmitidoUtc";

    private TicketX? LeerTicketXPorCobro(SqliteConnection db,SqliteTransaction tx,
        string origen,Guid cobroId)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=$"SELECT {ColumnasX} FROM TicketsX WHERE Origen=$origen AND CobroId=$cobro";
        cmd.Parameters.AddWithValue("$origen",origen);
        cmd.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
        TicketX? x;
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read())return null;
            x=LeerTicketX(r,false);
        }
        return x with {Devuelto=ObtenerCobroX(db,tx,origen,cobroId)?.Devuelto??true};
    }

    public ResultadoEmisionX EmitirTicketX(string codigo,string origen,Guid cobroId)
    {
        ExigirPermiso(PermisosAparcar.Cobro);
        codigo=NormalizarCodigoX(codigo);
        if(cobroId==Guid.Empty)throw new ArgumentException("Elegí un cobro confirmado.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        var previo=LeerTicketXPorCobro(db,tx,origen,cobroId);
        if(previo is not null)
        {
            if(previo.Talonario!=codigo)
                throw new InvalidOperationException("Ese cobro ya tiene un ticket X en otro talonario.");
            tx.Commit();
            return new ResultadoEmisionX(previo,false);
        }
        var cobro=ObtenerCobroX(db,tx,origen,cobroId);
        if(cobro is null || cobro.Value.Importe<=0 || cobro.Value.Devuelto)
            throw new InvalidOperationException("Solo se permite X para cobros positivos, confirmados y no devueltos.");
        long numero;
        using(var actualizar=db.CreateCommand())
        {
            actualizar.Transaction=tx;
            actualizar.CommandText="""
                UPDATE TalonariosX SET UltimoNumero=UltimoNumero+1
                WHERE Codigo=$codigo AND Habilitado=1 AND UltimoNumero<99999999
                RETURNING UltimoNumero;
                """;
            actualizar.Parameters.AddWithValue("$codigo",codigo);
            var dato=actualizar.ExecuteScalar();
            if(dato is null)
                throw new InvalidOperationException("El talonario X no existe, está deshabilitado o agotó su numeración.");
            numero=Convert.ToInt64(dato,CultureInfo.InvariantCulture);
        }
        var ahora=DateTimeOffset.UtcNow;
        var nuevo=new TicketX(Guid.NewGuid(),codigo,numero,origen,cobroId,
            cobro.Value.Importe,cobro.Value.Medio,cobro.Value.Descripcion,
            cobro.Value.Fecha,ahora,false);
        using(var insert=db.CreateCommand())
        {
            insert.Transaction=tx;
            insert.CommandText="""
                INSERT INTO TicketsX(Id,Talonario,Numero,Origen,CobroId,ImporteCentavos,
                     MedioPago,Descripcion,FechaCobroUtc,EmitidoUtc)
                VALUES($id,$talonario,$numero,$origen,$cobro,$importe,$medio,$descripcion,
                    $fechaCobro,$fechaEmision);
                """;
            insert.Parameters.AddWithValue("$id",nuevo.Id.ToString("D"));
            insert.Parameters.AddWithValue("$talonario",codigo);
            insert.Parameters.AddWithValue("$numero",numero);
            insert.Parameters.AddWithValue("$origen",origen);
            insert.Parameters.AddWithValue("$cobro",cobroId.ToString("D"));
            insert.Parameters.AddWithValue("$importe",nuevo.ImporteCentavos);
            insert.Parameters.AddWithValue("$medio",nuevo.MedioPago);
            insert.Parameters.AddWithValue("$descripcion",nuevo.Descripcion);
            insert.Parameters.AddWithValue("$fechaCobro",nuevo.FechaCobroUtc.ToString("O"));
            insert.Parameters.AddWithValue("$fechaEmision",ahora.ToString("O"));
            insert.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,"TicketX.Emitido",nuevo.Id.ToString("D"),
            $"X {codigo}-{numero:D8}, origen {origen}, no fiscal. Sin movimientos de caja.");
        tx.Commit();
        return new ResultadoEmisionX(nuevo,true);
    }

    public TicketX ObtenerTicketX(string codigo,long numero)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        codigo=NormalizarCodigoX(codigo);
        if(numero<1)throw new ArgumentOutOfRangeException(nameof(numero));
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasX} FROM TicketsX WHERE Talonario=$talonario AND Numero=$numero";
        cmd.Parameters.AddWithValue("$talonario",codigo);
        cmd.Parameters.AddWithValue("$numero",numero);
        TicketX ticket;
        using(var r=cmd.ExecuteReader())
        {
            if(!r.Read())throw new InvalidOperationException("No existe ese ticket X.");
            ticket=LeerTicketX(r,false);
        }
        var devuelto=ObtenerCobroX(db,null,ticket.Origen,ticket.CobroId)?.Devuelto??true;
        return ticket with {Devuelto=devuelto};
    }

    public Guid RegistrarIntentoImpresionX(Guid ticketId,bool copia,string motivo,string impresora)
    {
        ExigirPermiso(copia?PermisosAparcar.Reimpresion:PermisosAparcar.Impresion);
        if(ticketId==Guid.Empty)throw new ArgumentException("Ticket X obligatorio.");
        motivo=(motivo??"").Trim();
        if(copia && (motivo.Length is <8 or >250))
            throw new ArgumentException("La reimpresión exige motivo de 8 a 250 caracteres.");
        if(impresora is null || impresora.Trim().Length is <1 or >200)
            throw new ArgumentException("Seleccioná una impresora válida.");
        using var db=Abrir();using var tx=db.BeginTransaction();
        using(var check=db.CreateCommand())
        {
            check.Transaction=tx;
            check.CommandText="SELECT COUNT(*) FROM TicketsX WHERE Id=$id";
            check.Parameters.AddWithValue("$id",ticketId.ToString("D"));
            if(Convert.ToInt32(check.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                throw new InvalidOperationException("Ticket X inexistente.");
        }
        var id=Guid.NewGuid();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO ImpresionesX(Id,TicketXId,EsCopia,Motivo,Impresora,Estado,
                    Detalle,CreadaUtc)
                VALUES($id,$ticket,$copia,$motivo,$impresora,'Pendiente','',$fecha);
                """;
            cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            cmd.Parameters.AddWithValue("$ticket",ticketId.ToString("D"));
            cmd.Parameters.AddWithValue("$copia",copia?1:0);
            cmd.Parameters.AddWithValue("$motivo",motivo);
            cmd.Parameters.AddWithValue("$impresora",impresora.Trim());
            cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        AuditarSesion(db,tx,copia?"TicketX.Reimpresion":"TicketX.Impresion",ticketId.ToString("D"),
            copia?$"Solicitada copia. Motivo: {motivo}":"Solicitada impresión original X");
        tx.Commit();
        return id;
    }

    public void RegistrarResultadoImpresionX(Guid intentoId,bool enviado,string detalle)
    {
        ExigirPermiso(PermisosAparcar.Impresion);
        detalle=(detalle??"").Trim();
        if(detalle.Length>300)detalle=detalle[..300];
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE ImpresionesX SET Estado=$estado,Detalle=$detalle,TerminadaUtc=$fecha
            WHERE Id=$id AND Estado='Pendiente';
            """;
        cmd.Parameters.AddWithValue("$estado",enviado?"Enviado":"Error");
        cmd.Parameters.AddWithValue("$detalle",detalle);
        cmd.Parameters.AddWithValue("$fecha",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id",intentoId.ToString("D"));
        if(cmd.ExecuteNonQuery()!=1)
            throw new InvalidOperationException("La impresión no estaba pendiente o ya fue registrada.");
    }

    public IReadOnlyList<TicketX> ListarTicketsX(int max=150)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(max is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(max));
        using var db=Abrir();using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT {ColumnasX} FROM TicketsX ORDER BY EmitidoUtc DESC LIMIT $max";
        cmd.Parameters.AddWithValue("$max",max);
        var lista=new List<TicketX>();
        using var r=cmd.ExecuteReader();
        while(r.Read())lista.Add(LeerTicketX(r,false));
        return lista;
    }
}
