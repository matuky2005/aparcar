# Aparcar v0.24 — Auditoría de impresión X e integridad persistente

**Estado:** auditoría y corrección incorporadas al PR borrador. Todavía no se ha compilado WinUI ni ejecutado la batería C# sobre Windows desde los últimos cambios. Sin publicación de MSIX o artefactos.

## Hallazgo

Antes de esta etapa, el módulo de recibos operativos v0.21 ya imponía una sola impresión **ORIGINAL** por cobro mediante índice único. Sin embargo, `ImpresionesX` (tickets X no fiscales v0.19/v0.23) permitía crear **más de un intento ORIGINAL** para el mismo ticket.

Esto podía provocar dobles impresiones originales cuando un operador o integración reintentara después de un fallo de impresora, caída de proceso o incertidumbre del spooler. No duplicaba cobros ni números X, pero sí debilitaba la auditoría documental.

## Corrección

- Se añade **migración SQLite 18** (`InicializarV24IntegridadX`), después de la v0.21 / esquema 17, con respaldo consistente previo.
- El disparador `TR_TicketX_OriginalUnico` bloquea cualquier nueva fila `EsCopia=0` para un `TicketXId` que ya tenga un intento ORIGINAL, independientemente de su estado `Pendiente`, `Enviado` o `Error`.
- Se eligió un **trigger** y no un índice UNIQUE nuevo para no perder históricos anteriores que excepcionalmente contengan varios intentos originales. El historial anterior permanece y la regla sólo rechaza inserciones futuras.
- `RegistrarIntentoImpresionX` ahora informa anticipadamente que debe solicitarse una **COPIA** y traduce el error de restricción SQLite en una explicación orientada al cajero.
- `TicketXTieneIntentoOriginal` expone un indicador de consulta autorizado para estado y diagnóstico.
- El diagnóstico de restauraciones admite esquema 18. Los cobros, las facturas ARCA de homologación, la numeración X y la impresión normal del ingreso no se modifican.

**Importante:** un trabajo enviado al spooler no asegura que haya salido físicamente el papel. Nunca debe tratarse un error de spooler como permiso para emitir otro ORIGINAL; la recuperación se hace como COPIA con motivo y autorización.

## Verificaciones

- Pruebas C# añadidas en `tests/Aparcar.Tests/IntegridadXV24Tests.cs`: bloquea segundo original en `Pendiente`, `Enviado` y `Error`, permite copias autorizadas, rechazo mediante SQL directo y migración sin borrar duplicados históricos.
- Revisión estática de 105 referencias de eventos XAML a C#, nombres de controles duplicados, orden de migraciones, integridad de respaldo y restricción de CI.
- Prueba puntual SQLite independiente: una segunda impresión ORIGINAL es rechazada con `SQLITE_CONSTRAINT`, pero una COPIA sí puede insertarse.
- **Sin ejecución completa de tests .NET, compilación WinUI ni impresión física todavía.**

## Estado para siguiente hito

Antes de considerar una versión instalable: ejecutar el workflow de validación Windows `Aparcar - Validacion manual sin artefactos`, corregir fallos reales si aparecen, probar backup/restauración esquema 18 y testear 58/80 mm en impresoras reales. La generación de MSIX/ZIP seguirá desactivada hasta autorización posterior.

La emisión X sigue siendo un **documento no válido como factura**, sin CAE y sin sustituir las obligaciones fiscales.
