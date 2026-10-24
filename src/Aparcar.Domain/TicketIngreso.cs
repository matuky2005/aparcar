namespace Aparcar.Domain;

/// <summary>Ticket de ingreso. Token QR no contiene patente ni datos personales.</summary>
public sealed record TicketIngreso(
    long Numero,
    Guid EstadiaId,
    string Patente,
    string TipoVehiculo,
    string Sector,
    DateTimeOffset FechaIngresoUtc,
    string TokenQr);
