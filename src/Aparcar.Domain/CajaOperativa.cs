namespace Aparcar.Domain;

public sealed record TurnoCajaOperativo(
    Guid Id, string Operador, DateTimeOffset AperturaUtc,
    long FondoInicialCentavos, long CobradoCentavos,
    long EsperadoCentavos, bool Abierto);

public sealed record ArqueoCierre(
    Guid TurnoId, long EsperadoCentavos, long ContadoCentavos,
    long DiferenciaCentavos, DateTimeOffset FechaUtc, long NumeroCierreZ = 0);

public sealed record CierreZListado(
    long Numero, Guid TurnoId, string Operador, DateTimeOffset AperturaUtc,
    DateTimeOffset CierreUtc, long RecaudacionEfectivoCentavos,
    long EsperadoCentavos, long ContadoCentavos, long DiferenciaCentavos);

public sealed record CotizacionOperativa(
    Guid EstadiaId, long Ticket, string Patente, string TipoVehiculo,
    DateTimeOffset FechaCalculoUtc, DateTimeOffset VenceUtc,
    long ImporteCentavos, int TarifaVersion, Guid? TurnoId,
    bool CubiertoPorAbono=false);

public sealed record CobroEfectivoConfirmado(
    Guid Id, Guid EstadiaId, long Ticket, long ImporteCentavos,
    long EfectivoRecibidoCentavos, long VueltoCentavos, DateTimeOffset FechaUtc);

public sealed record CobroListado(
    long Ticket, string Patente, long ImporteCentavos,
    DateTimeOffset FechaUtc, string Operador);
