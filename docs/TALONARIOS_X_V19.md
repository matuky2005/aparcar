# Aparcar v0.19 — Talonarios y tickets X no fiscales

**Estado:** código de desarrollo, PR borrador. No habilita emisión comercial, no sustituye factura electrónica y no constituye un controlador fiscal homologado.

## Concepto

`Ticket X` es una constancia interna numerada sobre un **cobro ya confirmado** en el sistema, con identificación X y la advertencia destacada **DOCUMENTO NO VALIDO COMO FACTURA**. Puede usarse para seguimiento y atención al cliente solo en la medida en que no se lo presente como reemplazo del comprobante fiscal exigido. El flujo de emisión de facturas por WSFEv1 sigue completamente separado.

ARCA especifica que los recibos X que respaldan pagos previamente documentados por factura, y los documentos análogos X, deben indicar que no son válidos como factura. Aparcar no afirma que el ticket X sea fiscal ni que la impresora Windows sea un controlador fiscal.

## Talonarios

Se añade migración SQLite **esquema 16** con respaldo previo:

- `TalonariosX`: código de cuatro dígitos propio (ej. 0001), nombre/caja, último número emitido y estado de habilitación. No es un punto de venta de ARCA.
- `TicketsX`: identificador propio, código+secuencia, origen (Estacionamiento, Lavadero, Abono), referencia al cobro confirmado, importe, medio, detalle del servicio y fechas de cobro y emisión.
- Numeración de **00000001 a 99999999**, independiente por código de talonario; incremento e inserción dentro de la misma transacción.
- Restricción `UNIQUE(Origen,CobroId)`: no puede emitirse otro X para el mismo cobro ni reutilizar ese cobro en otro talonario. Reintentos idénticos devuelven el X existente sin volver a imprimir automáticamente ni incrementar secuencia.
- No se admite X sobre cobros inexistentes, importe cero o cobros devueltos. Si un cobro fue devuelto **después** de emitir X, el histórico permanece y la consulta para reimpresión lo identifica como **COBRO DEVUELTO**.
- No se crean filas en `CobrosCaja`, `MovimientosEfectivo`, `CobrosComplementarios` ni se altera `PendientesFiscales`. La emisión X **no cobra ni factura**.

## Uso en pantalla

1. Administrador: configura un talonario nuevo con código y nombre. No se pueden crear dos con el mismo código ni reiniciar secuencias.
2. Operador habilitado para cobrar: actualiza cobros y elige un pago confirmado no devuelto, por cualquiera de las tres áreas. Selecciona el talonario y elige **Emitir número X e imprimir**.
3. La base reserva número X de forma persistente **antes** de pedir la impresión. El driver Windows recibe un ticket de 58 u 80 mm con número, fecha, medio, concepto, total, letra X y las leyendas no fiscales. Si falla la impresora, **el número no se pierde ni se utiliza de nuevo**; requiere impresión de copia.
4. Supervisor/administrador con permiso `Tickets.Reimprimir`: elige histórico, informa un motivo de 8 a 250 caracteres e imprime con la leyenda **COPIA — REIMPRESION**.
5. Tabla `ImpresionesX`: conserva trabajos original/copia, impresora, motivo, estado `Pendiente/Enviado/Error` y error. **Enviado** indica aceptación del controlador Windows, no que haya salido papel.
6. Permisos existentes: `Configuracion` para crear talonario, `Caja.Cobrar` para emitir, `Tickets.Imprimir` para registrar originales y `Tickets.Reimprimir` para copias.

## Controles y límites

- No genera CAE, código QR ARCA, factura A/B/C ni autoriza un segundo cobro.
- X no se interpreta como cierre diario de controlador fiscal (Informe X/Z), ni como documento fiscal.
- Los documentos tributarios siguen siendo obligatorios cuando corresponda, aun si ya se entregó un X.
- Para múltiples terminales en red será necesario serializar numeración mediante un servidor o DB central; el SQLite actual es **monousuario/local**.
- No hay envío automático al cobrar, ni configuración de habilitación/deshabilitación o cambio de nombre de talonario todavía.
- Las impresoras 58 y 80 mm deben probarse físicamente, al igual que alimentación/corte, márgenes y fallos de papel.
- El nuevo esquema SQLite 16 se admite en el diagnóstico de restauración. No cambia bases previas destructivamente.
- CI Windows y pruebas C# de esta etapa **pendientes de ejecutar**. No se generan MSIX, ZIP ni artefactos y no se fusiona el PR.

### Fuentes normativas

- ARCA — Comprobantes / Situaciones especiales: https://www.arca.gob.ar/facturacion/comprobantes/situaciones-especiales.asp
- ARCA — Comprobantes válidos y no válidos: https://www.arca.gob.ar/facturacion/comprobantes/
- RG 3561: https://biblioteca.arca.gob.ar/search/query/norma.aspx?p=t%3ARAG%7Cn%3A3561%7Co%3A3%7Ca%3A2013%7Cf%3A09%2F12%2F2013
