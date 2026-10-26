# Aparcar v0.23 — Ticket X desde la pantalla de cobro

**Estado:** cambios de código cargados en el PR borrador. No se han compilado ni probado con impresora física. Sin facturación ARCA productiva.

## Objetivo

El cajero puede optar por emitir un **ticket X NO FISCAL** directamente al cobrar una salida, un lavado o un abono, sin abrir Configuración ni buscar el cobro después. La opción es deliberadamente **manual y desmarcada por defecto en cada operación**. No reemplaza una factura electrónica.

## Configuración administrativa

1. Crear talonario X (por ejemplo, `0001`) con la configuración desarrollada en v0.19.
2. Elegirlo en **«Talonario X predeterminado para cobrar»** y pulsar **«Guardar talonario X para cobros»**. El dato se almacena como preferencia `TicketX.TalonarioPredeterminado`, auditada bajo permiso `Configuracion`.
3. Configurar una impresora válida en Windows y el ancho 58/80 mm.

No existe elección silenciosa del primer talonario. Si el administrador no configura uno, el cajero puede continuar cobrando **sin X**, pero no puede marcar X y continuar sin corregirlo o desmarcarlo.

## Flujo operativo

- **Estacionamiento:** casilla `SalidaEmitirXCheck` en «Salida y cobro». El cajero confirma pago con F10. Tras persistirlo, emite el X del cobro. Cuando eligió X, no se imprime además el recibo estándar automático; el recibo histórico se conserva para consulta.
- **Lavadero:** casilla `LavEmitirXCheck` junto a «Confirmar cobro». Tras el cobro confirmado se emite el X de la orden.
- **Abonos:** casilla `AbEmitirXCheck` junto a «Cobrar y activar contrato». Tras el cobro confirmado se emite el X.
- **Prevalidación:** antes de invocar cobro se comprueba talonario habilitado, permiso de `Caja.Cobrar` y `Tickets.Imprimir`, e impresora Windows válida. Si no se cumple, no se ejecuta el cobro solicitado con X.
- **Commit primero, X después:** el cobro usa su transacción existente. Luego se usa `EmitirTicketX(talonario,origen,cobroId)`, que devuelve un X existente en reintentos y nunca duplica el cobro ni reinicia el número.
- **Fallo posterior:** si X o impresora fallan tras confirmar el pago, la pantalla avisa explícitamente **COBRO CONFIRMADO; NO VUELVAS A COBRAR**. Si el X llegó a registrarse, se conserva el número y se recupera mediante **COPIA** supervisada y auditada.
- **No duplicación de papel:** si el cajero eligió X en la salida, se omite imprimir automáticamente el recibo operativo común. Nunca se convierte un X en factura.
- **Limpieza:** las casillas vuelven a desmarcarse tras una operación correctamente confirmada.

## Base y permisos

No se necesita nueva migración: continúa esquema SQLite **16**. La preferencia se guarda en `PreferenciasDispositivos` con pista de auditoría. Los talonarios, tickets, reimpresiones, cobros y pendientes fiscales de la v0.19 se mantienen independientes.

## Validación

Se añadieron tests de configuración predeterminada, persistencia, selección explícita, rechazo de talonarios inexistentes e invariancia de numeraciones. Las pruebas originales de v0.19 cubren idempotencia, unicidad del cobro, devolución, historial y auditoría de impresión.

**Pendiente:** compilación WinUI y pruebas C# completas, ejecución con caja/impresora real, comprobación de 58/80 mm, recuperación tras desconexión de impresora, evaluación de concurrencia y homologación fiscal de ARCA. El proyecto continúa monousuario en desarrollo, sin instaladores, MSIX ni artefactos.

Un X puede documentar una operación a efectos internos, pero **no elimina ni sustituye la obligación de emitir y entregar el comprobante fiscal exigible**.
