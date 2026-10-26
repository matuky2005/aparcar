using System;
using System.Collections.Generic;
using System.Globalization;
using Aparcar.Domain;

namespace Aparcar.Infrastructure;

/// <summary>
/// Vista de consulta de cobros y documentos. Una factura AUTORIZADA en homologación
/// NO es un comprobante fiscal válido en producción.
/// </summary>
public sealed record EstadoDocumentalCobroV20(
    string Origen, Guid CobroId, long ImporteCentavos, DateTimeOffset FechaCobroUtc,
    string EstadoSeguimiento, string? TalonarioX, long? NumeroX,
    string? EstadoHomologacion, int? TipoHomologacion, int? PuntoVentaHomologacion,
    long? NumeroHomologacion, bool CaeDePrueba, bool Devuelto)
{
    public bool TieneTicketX => TalonarioX is not null && NumeroX is not null;
    public bool EsHomologacionAutorizada => EstadoHomologacion == "Autorizada" && CaeDePrueba;
}

/// <summary>
/// Control documental local. Solo lectura: no registra cobros, facturas ni X.
/// Las solicitudes WSFE en esta etapa son EXCLUSIVAMENTE de homologación.
/// </summary>
public sealed partial class AparcarDb
{
    public IReadOnlyList<EstadoDocumentalCobroV20> ListarEstadoDocumentalCobros(int max=150)
    {
        ExigirPermiso(PermisosAparcar.Reportes);
        if(max is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(max));

        using var db = Abrir();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.Origen,p.CobroId,p.ImporteCentavos,p.FechaUtc,p.Estado,
                   x.Talonario,x.Numero,
                   f.Estado,f.Tipo,f.PuntoVenta,f.Numero,f.Cae,
                   CASE WHEN p.Origen='Estacionamiento' THEN
                       EXISTS(SELECT 1 FROM DevolucionesCaja d WHERE d.CobroId=p.CobroId)
                   ELSE
                       EXISTS(SELECT 1 FROM DevolucionesComplementarias d WHERE d.CobroId=p.CobroId)
                   END AS Devuelto
            FROM PendientesFiscales p
            LEFT JOIN TicketsX x ON x.Origen=p.Origen AND x.CobroId=p.CobroId
            LEFT JOIN FacturasHomologacion f ON f.Origen=p.Origen AND f.CobroId=p.CobroId
            ORDER BY p.FechaUtc DESC,p.Id DESC LIMIT $max;
            """;
        cmd.Parameters.AddWithValue("$max",max);
        using var reader = cmd.ExecuteReader();
        var lista = new List<EstadoDocumentalCobroV20>();
        while(reader.Read())
        {
            lista.Add(new EstadoDocumentalCobroV20(
                reader.GetString(0),Guid.Parse(reader.GetString(1)),reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3),CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),
                reader.IsDBNull(6)?null:reader.GetInt64(6),
                reader.IsDBNull(7)?null:reader.GetString(7),
                reader.IsDBNull(8)?null:reader.GetInt32(8),
                reader.IsDBNull(9)?null:reader.GetInt32(9),
                reader.IsDBNull(10)?null:reader.GetInt64(10),
                !reader.IsDBNull(11),reader.GetInt64(12)!=0));
        }
        return lista;
    }
}
