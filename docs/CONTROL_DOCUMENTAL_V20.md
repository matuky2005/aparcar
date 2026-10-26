# Aparcar v0.20 — Control documental de cobros

**Estado:** propuesta en la rama de desarrollo y PR borrador; compilación de Windows y pruebas C# pendientes.

## Qué agrega
- Consulta consolidada (sin modificaciones a la base) de cobros de estacionamiento, lavadero y abonos desde `PendientesFiscales`.
- Por cada cobro: importe, fecha, devolución real, estado de seguimiento, número de Ticket X si existe y estado de solicitud WSFEv1 **solo de homologación**.
- Se diferencia claramente: **Ticket X no fiscal**, **solicitud de prueba ARCA**, y **factura de producción no verificada por esta versión**.
- Botón de consulta en Configuración para perfiles con permiso de reportes; evita que el operador interprete un X o CAE de prueba como factura real.
- Sin migración: se usan tablas existentes, sin duplicar cobros, emitir números, regenerar comprobantes ni modificar movimientos de caja.

## Reglas de interpretación
1. Un X no modifica el estado de seguimiento fiscal; continúa `PendienteDatosFiscales` mientras así figure.
2. Una solicitud `Autorizada` de homologación no es una factura válida en producción; el estado de seguimiento local permanece sin resolver.
3. Una devolución aparece aunque el X se haya emitido antes; no se borra el histórico.
4. El listado está limitado a 1–1000 operaciones y restringido al permiso `Reportes`.
5. Esta vista **no prueba** ausencia o presencia de facturas realizadas fuera de Aparcar. No representa cierre fiscal ni cumplimiento tributario.

## QA pendiente
Ejecutar tests xUnit `ControlDocumentalV20Tests` y compilación WinUI desde Windows; comprobar lectura de documentos históricos, permisos y rendimiento con base grande. No generar instaladores ni artefactos ni hacer merge a `main` todavía.
