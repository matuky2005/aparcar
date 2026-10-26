using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;

namespace Aparcar.Infrastructure;

/// <summary>
/// Solo indicadores operativos, sin saldos de caja ni datos tributarios.
/// Los ingresos recientes incluyen únicamente vehículos aún presentes.
/// </summary>
public sealed record SalidaRecienteV22(
    long Ticket,string Patente,string MedioPago,DateTimeOffset FechaUtc);

public sealed record PanelOperativoV22(
    long Presentes,long CapacidadHabilitada,long LibresHabilitados,
    string? OperadorTurno,IReadOnlyList<SectorEstacionamiento> Sectores,
    IReadOnlyList<Estadia> UltimosIngresos,IReadOnlyList<SalidaRecienteV22> UltimasSalidas)
{
    public bool CajaAbierta => OperadorTurno is not null;
}

/// <summary>
/// Lecturas acotadas y sin efectos secundarios. Nunca reserva tickets,
/// calcula cargos definitivos, confirma pagos ni cambia caja.
/// </summary>
public sealed partial class AparcarDb
{
    public PanelOperativoV22 ObtenerPanelOperativoV22(int maxFilas=15)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(maxFilas is <1 or >100)throw new ArgumentOutOfRangeException(nameof(maxFilas));
        using var db=Abrir();
        using var tx=db.BeginTransaction();
        long presentes;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM Estadias WHERE Estado='Activa'";
            presentes=Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        var sectores=new List<SectorEstacionamiento>();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT s.Nombre,s.Capacidad,s.Modo,s.Activo,
                    (SELECT COUNT(*) FROM Estadias e
                     WHERE e.Sector=s.Nombre AND e.Estado='Activa'),
                    (SELECT COUNT(*) FROM Plazas p
                     WHERE p.Sector=s.Nombre AND p.Habilitada=1
                     AND NOT EXISTS(SELECT 1 FROM OcupacionesPlaza o
                         WHERE o.Sector=p.Sector AND o.Codigo=p.Codigo
                           AND o.LiberadaUtc IS NULL))
                FROM Sectores s ORDER BY s.Nombre;
                """;
            using var r=cmd.ExecuteReader();
            while(r.Read())sectores.Add(new SectorEstacionamiento(
                r.GetString(0),r.GetInt32(1),r.GetString(2),r.GetInt32(3)==1,
                Convert.ToInt32(r.GetInt64(4),CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetInt64(5),CultureInfo.InvariantCulture)));
        }
        string? operador;
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="SELECT Operador FROM TurnosCaja WHERE Estado='Abierta' LIMIT 1";
            operador=cmd.ExecuteScalar() as string;
        }
        var ingresos=new List<Estadia>();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT Id,Ticket,Patente,TipoVehiculo,Sector,IngresoUtc,SalidaUtc,Estado
                FROM Estadias WHERE Estado='Activa'
                ORDER BY IngresoUtc DESC,Ticket DESC LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$max",maxFilas);
            using var r=cmd.ExecuteReader();
            while(r.Read())ingresos.Add(Leer(r));
        }
        var salidas=new List<SalidaRecienteV22>();
        using(var cmd=db.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT p.Ticket,e.Patente,m.MedioPago,p.FechaUtc
                FROM CobrosCaja p JOIN Estadias e ON e.Id=p.EstadiaId
                JOIN CobroMedios m ON m.CobroId=p.Id
                ORDER BY p.FechaUtc DESC,p.rowid DESC LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$max",maxFilas);
            using var r=cmd.ExecuteReader();
            while(r.Read())salidas.Add(new SalidaRecienteV22(
                r.GetInt64(0),r.GetString(1),r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3),CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)));
        }
        tx.Commit();
        long capacidad=0,libres=0;
        foreach(var sector in sectores)
        {
            if(!sector.Activo)continue;
            capacidad=checked(capacidad+sector.Capacidad);
            libres=checked(libres+sector.Libres);
        }
        return new PanelOperativoV22(presentes,capacidad,libres,operador,
            sectores,ingresos,salidas);
    }

    /// <summary>
    /// Búsqueda por número exacto o prefijo normalizado de patente.
    /// No se cobra ni selecciona automáticamente entre múltiples resultados.
    /// QR completo se resuelve con BuscarActiva al entrar a la pantalla de salida.
    /// </summary>
    public IReadOnlyList<Estadia> BuscarPresentesRapidoV22(string texto,int max=25)
    {
        ExigirPermiso(PermisosAparcar.Consulta);
        if(max is <1 or >100)throw new ArgumentOutOfRangeException(nameof(max));
        texto=(texto??"").Trim();
        if(texto.Length is <1 or >40)throw new ArgumentException(
            "Ingresá patente o número de ticket (máximo 40 caracteres).");
        var patente=NormalizarPatente(texto);
        if(patente.Length==0)throw new ArgumentException("No se encontró un identificador válido.");
        var numero=texto.TrimStart('0');
        if(numero.Length==0)numero="0";
        using var db=Abrir();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,Ticket,Patente,TipoVehiculo,Sector,IngresoUtc,SalidaUtc,Estado
            FROM Estadias
            WHERE Estado='Activa' AND
                (CAST(Ticket AS TEXT)=$numero OR PatenteNormalizada LIKE $prefijo)
            ORDER BY CASE WHEN CAST(Ticket AS TEXT)=$numero OR PatenteNormalizada=$patente
                          THEN 0 ELSE 1 END,
                     IngresoUtc DESC,Ticket DESC
            LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$numero",numero);
        cmd.Parameters.AddWithValue("$patente",patente);
        cmd.Parameters.AddWithValue("$prefijo",patente+"%");
        cmd.Parameters.AddWithValue("$max",max);
        using var r=cmd.ExecuteReader();
        var encontrados=new List<Estadia>();
        while(r.Read())encontrados.Add(Leer(r));
        return encontrados;
    }
}
