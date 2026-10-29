namespace Aparcar.Domain;

public sealed record Estadia(
    Guid Id,
    long Ticket,
    string Patente,
    string TipoVehiculo,
    string Sector,
    DateTimeOffset IngresoUtc,
    DateTimeOffset? SalidaUtc,
    string Estado);

/// <summary>Comprobante exclusivamente demostrativo: no representa dinero recibido.</summary>
public sealed record SalidaPrueba(
    long Ticket,
    string Patente,
    long ImporteSimuladoCentavos,
    DateTimeOffset SalidaUtc);
