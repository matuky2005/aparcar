# Aparcar v0.22 — Panel operativo de estacionamiento

**Estado:** código en rama de desarrollo, PR borrador. Sin merge, artefactos ni instaladores. Compilación Windows y ejecución de tests xUnit pendientes.

## Interfaz de Inicio
- Resumen de vehículos presentes, disponibilidad total de sectores habilitados y estado **abierto/cerrado** del turno. No se muestran saldos ni se elude la política de caja ciega.
- Ocupación por sector, incluyendo modo Libre/Asignado y estado Habilitado/Suspendido. Disponibilidad de sectores asignados se calcula con plazas **efectivamente habilitadas y desocupadas**, no solo capacidad teórica.
- Últimos ingresos **que continúan activos** y últimas salidas con **cobro confirmado**. Se marca **DEVUELTO** si ese cobro fue reintegrado; no se presentan como totales fiscales.
- Búsqueda acotada por número de ticket exacto o prefijo de patente normalizada: devuelve hasta 25 registros activos. Ante varias coincidencias se debe seleccionar manualmente una.
- Un clic permite estimar tarifa **sin cobrar**. Se vuelve a consultar la estadía para descartar selecciones obsoletas; la cotización tiene vencimiento y el precio definitivo se recalcula al cobrar.
- El botón **Preparar salida (sin cobrar)** verifica nuevamente que la estadía siga activa, abre el formulario de salida y rellena ticket. El cobro sigue dependiendo de la validación manual y `ConfirmarPago`, con idempotencia conservada.
- Atajos previos de ingreso/cobro y tickets continúan funcionando. Se aplican permisos de `Vehiculos.Consultar` y `Caja.Cobrar` a operaciones.
- El panel usa consultas de lectura en transacción SQLite, limita la cantidad de filas recientes y no ejecuta ningún `INSERT`, `UPDATE`, emisión X ni autorización WSFE.

## Límites y pendientes
- Búsqueda por prefijo es literal sobre la patente normalizada y muestra como máximo 25 coincidencias; no hace selección masiva ni cierre múltiple.
- Las últimas salidas muestran solo cobros de **estacionamiento** y no lavadero/abonos. Los montos no se exponen en el panel.
- Las tarifas configuradas por sector/franja se respetan a través de la cotización existente `CotizarSalida`; no presentar un precio general como si aplicara a todos los sectores.
- No hay actualización automática entre equipos; la base SQLite actual sigue siendo local/monousuario. Actualización manual al navegar, cobrar o ingresar.
- Probar en Windows navegación, foco, teclado, tamaños de ventana, permisos de rol, impresoras y cambios concurrentes de estadía.
- Tests C# propuestos en `PanelOperativoV22Tests` para resumen inicial, prefijos, ocupación de plazas y salida confirmada.
