namespace Aparcar.Domain;

public static class MediosPago
{
    public const string Efectivo = "Efectivo";
    public const string Debito = "Debito";
    public const string Credito = "Credito";
    public const string Transferencia = "Transferencia";
    public const string Qr = "QR";

    public static bool EsValido(string medio) =>
        medio is Efectivo or Debito or Credito or Transferencia or Qr;
}

public sealed record CobroConfirmadoV4(
    Guid Id, Guid EstadiaId, long Ticket, string MedioPago,
    long ImporteCentavos, long EntregadoCentavos, long VueltoCentavos,
    string Referencia, DateTimeOffset FechaUtc);

public sealed record MovimientoCajaManual(
    Guid Id, Guid TurnoId, string Tipo, long ImporteCentavos,
    string Motivo, string Operador, DateTimeOffset FechaUtc);

public sealed record DevolucionEfectivo(
    Guid Id, Guid CobroId, Guid TurnoId, long Ticket, long ImporteCentavos,
    string Motivo, string AutorizadoPor, DateTimeOffset FechaUtc);

public sealed record ResumenCajaV4(
    Guid TurnoId, string Operador, long FondoInicialCentavos,
    long CobrosEfectivoCentavos, long CobrosElectronicosCentavos,
    long DepositosCentavos, long RetirosCentavos,
    long DevolucionesEfectivoCentavos, long EfectivoEsperadoCentavos);

public sealed record LineaRecaudacion(
    long Ticket, string Patente, string MedioPago, long ImporteCentavos,
    DateTimeOffset FechaUtc, string Operador, bool Devuelto,
    string Origen = "Estacionamiento");

public sealed record ResumenRecaudacion(
    long CobrosBrutosCentavos, long DevolucionesCentavos,
    long CobrosNetosCentavos, long EfectivoCentavos, long ElectronicosCentavos,
    IReadOnlyList<LineaRecaudacion> Lineas);
