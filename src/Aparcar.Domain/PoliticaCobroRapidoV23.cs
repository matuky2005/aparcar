namespace Aparcar.Domain;

/// <summary>
/// Validación previa compartida por la salida completa y la caja rápida.
/// No registra ni acredita cobros; ConfirmarPago vuelve a validar los datos
/// y recalcula la cotización dentro de la transacción SQLite.
/// </summary>
public sealed record DatosCobroRapidoV23(
    string MedioPago,long EntregadoCentavos,long VueltoEstimadoCentavos);

public static class PoliticaCobroRapidoV23
{
    public static DatosCobroRapidoV23 Validar(long totalCentavos,string medio,
        long entregadoCentavos,string? referencia,bool verificado)
    {
        if(totalCentavos<0)throw new ArgumentOutOfRangeException(nameof(totalCentavos));
        if(!MediosPago.EsValido(medio))
            throw new ArgumentException("Seleccioná un medio de pago válido.",nameof(medio));
        if(entregadoCentavos<0)
            throw new ArgumentOutOfRangeException(nameof(entregadoCentavos));
        if(medio==MediosPago.Efectivo)
        {
            if(entregadoCentavos<totalCentavos)
                throw new InvalidOperationException("El efectivo entregado no cubre el total.");
            return new DatosCobroRapidoV23(medio,entregadoCentavos,
                checked(entregadoCentavos-totalCentavos));
        }
        // Ningún botón, tecla ni cambio de campo puede activar la verificación.
        if(!verificado)
            throw new InvalidOperationException(
                "Confirmá explícitamente que el pago externo se acreditó.");
        if(string.IsNullOrWhiteSpace(referencia) || referencia.Trim().Length is <4 or >200)
            throw new ArgumentException(
                "Registrá una referencia real de pago de 4 a 200 caracteres.");
        if(entregadoCentavos!=totalCentavos)
            throw new InvalidOperationException(
                "El pago externo debe corresponder al importe exacto cotizado.");
        return new DatosCobroRapidoV23(medio,totalCentavos,0);
    }
}
