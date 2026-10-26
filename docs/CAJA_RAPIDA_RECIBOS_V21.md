# Aparcar v0.21 — Caja rápida y recuperación segura de recibos

**Estado:** rama de desarrollo; PR borrador. Sin merge ni instalador. Compilación WinUI y tests C# pendientes.

## Mejoras operativas
- F2: ir a Nuevo ingreso; F4: Salida y cobro; F8: presentes; F5: buscar en Salida; F10: guardar ingreso o confirmar cobro **solo si la acción está habilitada**.
- Enter en Ingreso: patente → tipo → sector → plaza si aplica → botón guardar. No emite al enfocar botón: la confirmación permanece explícita.
- Enter en Salida: buscar → medio → efectivo o referencia electrónica → verificación manual (casilla) → botón confirmar. Verificación de pagos electrónicos **nunca automática**; hay que tildar con clic o Espacio.
- Los atajos se ignoran mientras la sesión está bloqueada; los permisos de usuario siguen aplicándose. El guardado/cobro impide operaciones simultáneas desde la misma pantalla.
- Configuración previa de impresora Windows 58/80 mm y opción existente de impresión automática. Los eventos de envío al controlador no garantizan papel impreso.

## Historial persistente de recibos
- Migración SQLite **esquema 17** con respaldo previo y verificación de restauración actualizada.
- Una fila por intento de ORIGINAL y múltiples COPIAS autorizadas, cada una con motivo, fecha, actor, impresora y estado `Pendiente/Enviado/Error`.
- Se registra `Pendiente` **antes** de imprimir; solo un intento original por cobro, incluso con reintentos/cierre brusco (`UNIQUE(CobroId) WHERE EsCopia=0`).
- Luego de cualquier error, no reintentar el cobro ni generar otro original. El supervisor puede enviar **COPIA** con motivo obligatorio de 8 a 250 caracteres. Los documentos impresos distinguen ORIGINAL/COPIA y advierten si el cobro fue devuelto.
- Cuando la impresión automática está desactivada, el operador autorizado puede imprimir el ORIGINAL desde el historial si no existió intento anterior. Antes de imprimir operaciones antiguas, debe verificar que no tengan ya un papel impreso por versiones anteriores.
- Se consultan hasta 100 cobros recientes con permisos de `Consulta`; el original requiere `Tickets.Imprimir`, la copia requiere `Tickets.Reimprimir`.
- La tabla de impresiones no genera ingresos, salidas, movimientos de caja, facturas, CAE ni tickets X.
- Queda separado el recibo no fiscal del Ticket X y de las facturas electrónicas: v0.21 solo tiene emisión WSFEv1 en homologación.

## Pruebas propuestas
`RecibosOperativosV21Tests` prueba unicidad del original, auditoría de copia, inmutabilidad del resultado, estado de error, saldo de caja intacto, persistencia tras reinicio y validaciones. **No ejecutadas en Windows**; también faltan pruebas físicas de impresoras, permisos por rol y atajos/teclado.
