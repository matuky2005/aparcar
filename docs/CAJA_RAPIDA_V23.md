# Aparcar v0.23 — Caja rápida y emisión X opcional

**Estado:** branch de desarrollo y PR #1 en borrador. **Sin merge ni instaladores.** Compilación WinUI y ejecución de tests xUnit pendientes.

## Operación en la pantalla Inicio
1. Buscar patente/ticket y seleccionar el vehículo de las coincidencias activas.
2. Elegir **Preparar caja rápida (sin cobrar)**: consulta la estadía y genera una cotización de dos minutos. Si no existe turno abierto, el botón de confirmación queda inhabilitado.
3. Elegir efectivo, débito, crédito, transferencia o QR; si es efectivo, ingresar dinero recibido y ver el vuelto estimado. Si es electrónico, aportar referencia real y marcar manualmente la casilla de verificación después de constatar la acreditación en el proveedor.
4. Opcional: marcar **Emitir Ticket X NO FISCAL**, desmarcada por defecto. Se comprueba el talonario predeterminado, permisos e impresora **antes del cobro**. Si no se elige X, se respeta impresión automática del recibo no fiscal.
5. **Confirmar cobro y salida** es la única acción que toca caja. Usa el `ConfirmarPago` transaccional existente, que recalcula cotización, valida operaciones y garantiza idempotencia por clave. La cotización de interfaz se invalida inmediatamente al confirmarse, antes de cualquier impresión o actualización de pantalla.

## Seguridad y comportamiento
- **Nunca** marca acreditaciones externas por sí solo; Enter avanza al check de verificación y su activación exige Espacio o clic.
- Nuevo `PoliticaCobroRapidoV23.Validar` compartido por la caja rápida y el formulario completo. Valida efectivo suficiente, pago externo de importe exacto, referencia 4–200 caracteres y comprobación explícita. `ConfirmarPago` también valida dentro de SQLite.
- Cancelar preparación, cambiar resultado, buscar otro vehículo o cerrar sesión limpia la cotización y el token. No hace movimientos.
- La vista completa sigue disponible desde el botón **Usar formulario completo**. No se pierden opciones avanzadas.
- No dispara Ticket X cuando el operador no lo solicita, ni mezcla Ticket X con factura ARCA; el recibo y el X son NO FISCALES.
- Si el dinero quedó registrado y falla después imprimir o actualizar pantalla, muestra `COBRO CONFIRMADO`. Nunca sugiere volver a cobrar; usa el historial para recuperar impresiones.
- Las notas de crédito/facturas ARCA productivas no se implementan ni habilitan en esta etapa.

## QA
- Tests añadidos `PoliticaCobroRapidoV23Tests`: cash, vuelto, cero por abono, monto insuficiente, débito/crédito/transferencia/QR, verificación externa y referencias, importes exactos, tipos inválidos.
- Pendientes: compilar Windows/WinUI, correr xUnit, probar foco y teclado con pantalla pequeña, impresoras 58/80 mm, Ticket X y permisos de cada rol. Hasta comprobarlo, no publicar ni fusionar a `main`.
