namespace Aparcar.Domain;

/// <summary>Se conserva el total real en la base, pero no se muestra antes del arqueo ciego.</summary>
public sealed record EstadoControlCaja(Guid TurnoId,bool Ciega);

public sealed record AutorizacionReimpresion(
    Guid Id,Guid EstadiaId,long Ticket,string Autoriza,string Motivo,DateTimeOffset FechaUtc);

/// <summary>
/// Registro interno de operación a facturar. NO es un comprobante fiscal,
/// no indica emisión ante ARCA y no contiene CAE.
/// </summary>
public sealed record PendienteFiscal(
    Guid Id,string Origen,Guid CobroId,long ImporteCentavos,
    DateTimeOffset FechaUtc,string Estado);
