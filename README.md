# Aparcar

Sistema de estacionamientos para Windows, con diseño para incorporar lavadero, abonados y varias cajas.

> **Estado: código de desarrollo v0.11 — NO apto para uso comercial ni cobros reales.**

## Etapa v0.5: usuarios, permisos y auditoría

**Código validado en Windows:** 26 pruebas automáticas y compilación WinUI 3 correctas en el [run de validación](https://github.com/matuky2005/aparcar/actions/runs/40122769179). No se publicaron archivos descargables.

- Al arrancar por primera vez, se debe crear un **administrador** con contraseña propia; sin él, las operaciones de negocio están bloqueadas.
- Inicio de sesión por empleado; roles Administrador, Supervisor, Cajero, Lavadero y Consulta, con verificación de permisos **también en la capa de persistencia**.
- Contraseñas cifradas irreversiblemente mediante PBKDF2-SHA256 con sal aleatoria; bloqueo temporal por cinco fallos, sin contraseñas predefinidas.
- Administrador: creación y mantenimiento de usuarios, revocación de acceso y restablecimiento de claves. Se impide desactivar al último administrador activo.
- Auditoría enlazada por hashes de ingreso/salida, cobros, ajustes de tarifa, apertura/cierre de caja, devoluciones, cambios de usuarios e inicios de sesión.
- Sesiones con expiración por inactividad. Los permisos de una cuenta desactivada o cuyo rol cambia dejan de habilitar operaciones.
- Migración de esquema a versión 5 sin vaciar datos: respaldos automáticos internos antes de migrar.

**Limitaciones:** los registros de auditoría pueden alterarse por quien controla físicamente el archivo SQLite; la cadena ayuda a detectar cambios, no es protección criptográfica externa. Quedan pendientes permisos granulares por sucursal, segunda caja, autenticación de supervisión de transacciones, cifrado/protección del directorio de datos y pruebas reales del operador.

### Regla de CI durante el desarrollo

El workflow `Aparcar - Validacion manual sin artefactos` **no se ejecuta por cada push**; se invoca manualmente cuando queremos verificar código. No produce ZIP, MSIX ni usa `upload-artifact`. Los instaladores volverán a generarse cuando acordemos un hito importante del proyecto.

## Etapa v0.11: duplicados impresos y consulta ARCA en homologación

**Validación:** [GitHub Actions #40169593413](https://github.com/matuky2005/aparcar/actions/runs/40169593413) — **55/55 pruebas aprobadas** y compilación WinUI 3 correcta, **sin artefactos descargables**. El workflow queda exclusivamente manual.

### Reimpresión de tickets
- Una reimpresión que ya fue autorizada por administrador/supervisor con motivo imprime en el papel la leyenda **DUPLICADO - REIMPRESION** y usa un nombre de trabajo diferenciado.
- Se conservan el ticket y QR original; la autorización y el resultado del controlador se registran en SQLite y auditoría.
- La confirmación de envío al controlador **no implica impresión física comprobada**. Faltan pruebas en impresoras térmicas 58/80 mm, lectores QR y cortes automáticos.

### ARCA — Primera conexión real, SOLO LECTURA / HOMOLOGACIÓN
- Se incorpora `ArcaHomologacion`, cliente C# que prepara un **TRA** para el servicio `wsfe`, lo firma con **CMS/PKCS#7** usando certificado PFX con clave privada y consulta **WSAA loginCms**. Luego usa Token, Sign y CUIT en **WSFEv1 FECompUltimoAutorizado**.
- Los únicos endpoints del cliente son los de **homologación** de WSAA y WSFEv1. No existe en este módulo método de emisión de CAE, confirmación de comprobante o invocación de producción.
- La pantalla de Configuración permite ingresar ruta de PFX de homologación, contraseña, CUIT, punto de venta y tipo (A=1, B=6, C=11). **La contraseña se borra del formulario después de consultar**. Ni certificado, contraseña, CMS, Token ni Sign se guardan en la base, registros o reportes.
- La consulta exige rol administrador y muestra únicamente el último número confirmado o un error resumido, sin exponer las credenciales SOAP.
- Se agregaron cuatro pruebas automatizadas de creación del TRA, firma CMS verificada criptográficamente, respuesta correcta WSAA/WSFEv1, error fiscal y rechazo de CUIT inválido, usando certificado temporal y un `HttpMessageHandler` simulado. **No se utilizaron credenciales de un contribuyente ni se probó una conexión real a ARCA.**
- Para conectarse de verdad, gestionar en ARCA el certificado de **homologación** mediante WSASS y vincularlo a WSFE. No subir certificados PFX, claves, contraseñas ni tokens al repositorio.

### Pendiente para emisión real
- Autorización `FECAESolicitar`, consulta de comprobantes, determinación fiscal de A/B/C, datos de emisor/receptor, comprobantes asociados, IVA, condición tributaria, CAE/vencimiento y notas de crédito. Deben respetarse puntos de venta y numeración correlativa de ARCA.
- Reintentos ante fallas de red y conciliación previa para impedir facturas duplicadas; persistencia inmutable de las respuestas oficiales y pruebas reales de homologación con un certificado autorizado.
- Recién después de homologar y auditar la integración se evaluará el endpoint de producción.

Fuentes oficiales: https://www.arca.gob.ar/ws/documentacion/wsaa.asp ; https://www.arca.gob.ar/ws/documentacion/ws-factura-electronica.asp .

**Aparcar sigue sin estar habilitado para cobros comerciales ni emisión fiscal. No publicar Portable/MSIX todavía.**

## Etapa v0.10: controles antifraude y preparación fiscal

**Validación:** [GitHub Actions #40159018401](https://github.com/matuky2005/aparcar/actions/runs/40159018401) — **51/51 pruebas aprobadas**; compilan dominio, persistencia y WinUI. **Cero artefactos generados.** Se restauró el workflow a modo manual.

### Caja ciega
- Al abrir un turno se puede elegir **caja ciega**; administrador puede hacerla **obligatoria** para todos los turnos con caja cerrada.
- La política se aplica en SQLite incluso cuando un cajero intenta abrir turno sin modalidad ciega.
- En un turno ciego, cajeros y otros roles no supervisores **no ven el efectivo esperado ni los totales de caja antes de cerrar**. El cajero primero declara el contado; después del cierre se registra el esperado y la diferencia reales.
- Administrador/supervisor pueden consultar el saldo para supervisión. El sistema sigue siendo local monousuario y **no tiene una caja cerrada por credencial, ni caja ciega criptográficamente aislada**: para un control real hacen falta dispositivos independientes, aislamiento de datos y auditoría externa.

### Reimpresión autorizada
- La reimpresión manual de tickets requiere rol **Supervisor/Administrador**, número de ticket válido y motivo de al menos 8 caracteres.
- Se registra autorización individual y evento encadenado de auditoría **antes** de intentar imprimir; el resultado del driver se sigue registrando en `ImpresionesTicket`.
- **Pendiente:** estampar físicamente “DUPLICADO” en la reimpresión, comprobar QR con lectores reales y verificar la impresora de 58/80 mm en el hardware destino.

### Registro previo a facturación
- En la **misma transacción del cobro confirmado** se conserva una operación pendiente única por cobro positivo de Estacionamiento, Lavadero o Abono.
- Los importes cero no generan pendiente fiscal. Los reintentos del mismo cobro no duplican el registro.
- Una devolución de efectivo pasa el pendiente a `RevisionDevolucion` para impedir tratarlo silenciosamente como operación normal.
- La lista aparece en Reportes. **NO se emiten facturas, no se conectó ARCA, no se obtiene CAE y no hay validación de datos fiscales de emisor/receptor.** Se debe implementar y auditar WSAA/WSFEv1, las condiciones de IVA, punto de venta, comprobante A/B/C, factura/nota de crédito y credenciales antes de operar comercialmente.
- La migración v10 conserva los datos anteriores y el verificador de restauración acepta esquemas 1 a 10.

**Sin ZIP ni MSIX hasta el hito de entrega.** El código continúa en rama y PR en borrador.

## Etapa v0.9: tarifas por sector, franjas y recuperación de datos

**Validación técnica:** [GitHub Actions #40157463005](https://github.com/matuky2005/aparcar/actions/runs/40157463005) · **46/46 tests aprobados**, compilación WinUI correcta, **sin paquetes publicados**. Workflow solo manual.

### Tarifas por sector y horario
- Cada sector tiene un horario nocturno configurable `HH:mm` (predeterminado 20:00–08:00). El resto del día es diurno.
- Se definen precios separados por **sector / clase de vehículo / franja (Diurna o Nocturna)**: primera hora, fracción, minutos, máximo 24 h, cortesía.
- Los cambios generan **versiones inmutables**. En la transacción de ingreso se vincula al vehículo la tarifa de sector/franja vigente. Nunca cambia el precio histórico tras modificar horarios o tarifas.
- Si no se configuró una tarifa sectorial para esa franja, se usa la **tarifa general ya versionada** de ese tipo de vehículo.
- El cálculo existente cobra **toda la estadía conforme a la franja de entrada**: no prorratea entre tramos diurnos/nocturnos, feriados ni horarios especiales. Este aspecto requiere validación comercial antes de activarse en producción.
- **Zona horaria:** por ahora depende del reloj local de Windows. En el futuro se configurará explícitamente por sucursal.
- **Abonados:** la cobertura de abono vigente tiene prioridad y bonifica el importe si cubre toda la estadía, de acuerdo con las reglas actuales.

### Respaldos y restauración guiada
- Configuración incorpora **Examinar archivo .db**, verificar estructura, integridad SQLite y relaciones; informa versión, estadías y SHA-256.
- La restauración la **prepara un administrador** y requiere que no haya turno de caja abierto. No sustituye la base actual mientras el sistema sigue funcionando.
- Al **reiniciar Aparcar** se vuelve a comprobar el archivo preparado y su SHA-256; se preserva una copia consistente de la base actual en `Aparcar/Datos/antes_restauracion_*.db`, luego se reemplaza usando la API de respaldo SQLite y se verifica la restaurada.
- Una copia manipulada o corrupta **no se aplica**. Los archivos pendientes se mantienen para diagnóstico. Una restauración puede reemplazar estadías, usuarios y cobros posteriores al respaldo; requiere confirmación explícita y debe coordinarse con la operación.
- Se admiten respaldos de esquema 1 a 9 y se migran posteriormente mediante los pasos versionados. No se puede restaurar en esta versión una base de un esquema futuro.
- La verificación SHA-256 protege contra corrupción accidental **dentro del flujo**, no contra un atacante con control del directorio local. Para producción hacen falta copias fuera del equipo, control de acceso, cifrado y procedimientos probados de recuperación.

### Próximas verificaciones
Pruebas reales con operador y hardware de impresión térmica, tarifas que se dividen por tramos horarios, estrategia y recuperación de bases tras pérdida de disco, control de datos de terminal multicaja y prevención de cierres incorrectos. **No usar para cobros comerciales sin esa validación.**

## Etapa v0.8: sectores, capacidad y plazas

**Validación de código:** [GitHub Actions #40150856322](https://github.com/matuky2005/aparcar/actions/runs/40150856322) · **38/38 pruebas aprobadas** y compilación WinUI correcta. **Sin artefactos publicados.** El workflow vuelve a estar reservado a ejecución manual.

- **Sector configurable:** nombre, capacidad de 1 a 100.000, habilitado/deshabilitado y modalidad **Libre** (aforo por sector) o **Asignado** (plazas individuales).
- **Migración v8:** antes de modificar la base se genera un respaldo consistente; se crea el sector `General` y se conservan los nombres de sectores antiguos que figuran en las estadías históricas.
- **Control transaccional de aforo:** el registro de un vehículo exige que exista un sector habilitado, con capacidad libre. Se comprueba dentro de la misma transacción SQLite que guarda la estadía.
- **Asignación individual:** plazas con código y estado habilitada/inhabilitada. El operador puede elegir una plaza libre o dejarla vacía para asignación automática; dos vehículos no pueden ocupar la misma plaza simultáneamente.
- **Liberación al finalizar:** al registrar el cobro de salida o la salida de prueba se libera la plaza en la misma transacción que finaliza la estadía.
- **Protecciones de configuración:** impide reducir capacidad por debajo de ocupación, desactivar/cambiar de modo un sector ocupado o inhabilitar una plaza ocupada. Solo un administrador autorizado modifica la configuración.
- **Operación WinUI:** listado de ocupación, configuración de sectores y plazas, selector de sector en Ingreso y foco con **Enter: patente → categoría → sector → plaza (si corresponde) → guardar**.
- **Impresión:** los tickets de ingreso muestran sector y, si corresponde, la plaza física asignada. Se mantiene el QR de acceso sin cambiarlo.

**Limitaciones:** el aforo es por cantidad de vehículos, sin reservas de espacio ni plazas por dimensiones; no hay zonificación multipiso, bloqueo por mantenimiento prolongado ni sincronización entre varias cajas. Las reglas de horario, precios diferenciados por sector, restauración guiada de respaldos y pruebas con impresoras físicas permanecen pendientes.

## Etapa v0.7 — Abonos avanzados y devoluciones

**Validación de código:** GitHub Actions [ejecución 40143983902](https://github.com/matuky2005/aparcar/actions/runs/40143983902) · **34/34 pruebas aprobadas**, compilación WinUI correcta, **sin artefactos publicados**.

- **Hasta cinco vehículos por contrato** de un mismo tipo (auto, moto o camioneta). El administrador puede agregar/quitar patentes **antes de activar** el abono. Una vez cobrado queda congelada la lista de vehículos autorizados.
- **Renovaciones anticipadas:** se crea un contrato pendiente que copia las patentes autorizadas del contrato anterior; tras pagar, comienza **al vencer la cobertura anterior**. Si ya venció, comienza desde el nuevo pago. Se comprueba que ningún vehículo tenga coberturas activas superpuestas.
- **Planes nocturnos:** junto con modalidad 24 horas, se incorpora cobertura de **20:00 a 08:00** según el huso horario local de la PC del estacionamiento. Se bonifica el estacionamiento únicamente cuando **toda la estadía** cae dentro de una noche cubierta y del mismo contrato vigente. No hay cobros prorrateados ni división de una estadía entre dos abonos consecutivos.
- **Devoluciones de Lavadero y Abonos:** únicamente **completas, en efectivo, con PIN del supervisor** y en caja abierta. Nunca se declara automáticamente un reembolso bancario o de tarjeta. Se bloquea la devolución de lavados entregados y de contratos de abono que registren estadías posteriores a la activación. Cada devolución se registra una sola vez, con auditoría, y cancela la orden o contrato correspondiente.
- **Caja y reportes:** las devoluciones complementarias restan efectivo esperado y figuran como movimientos negativos en recaudación Excel/PDF por su fecha efectiva.
- **Migración SQLite v7:** respaldo previo, copia de la patente principal de contratos antiguos, conservación de cobros, estadías, órdenes y tarifas. No se borran datos de v0.6.

**Limitaciones de producción:** falta vinculación probatoria y conciliación automática con pagos externos, control de horarios por sucursal en lugar de depender del PC, devoluciones parciales, pruebas de impresión en hardware, controles de manipulación de base fuera del equipo, reportes fiscales y pruebas de integración completas en un estacionamiento real.

El workflow sigue **manual**. No se generan MSIX, portables ni otros artefactos hasta un hito de producto.

## Validación v0.6

**Código validado en Windows:** 29/29 pruebas automáticas y compilación WinUI 3 exitosas en [GitHub Actions #40128096125](https://github.com/matuky2005/aparcar/actions/runs/40128096125). **Cero artefactos publicados.** Aún falta probar estas pantallas y sus flujos en un equipo físico antes de considerarlos aptos para producción.

## Etapa v0.6: Lavadero y Abonados

Sin generar ZIP/MSIX ni publicar versiones instalables, se incorporan nuevos módulos a la misma base SQLite.

### Lavadero
- Catálogo de servicios por clase de vehículo, con **precios versionados**. Las órdenes existentes conservan su precio original.
- Órdenes independientes o vinculadas a una estadía activa (misma patente y clase).
- Operario, observaciones e historial de estados: pendiente → en proceso → terminado → entregado. Cancelación solo antes de iniciar, y nunca de una orden cobrada.
- Cobros separados del estacionamiento; efectivo o medios externos manualmente verificados. **No se cobra dos veces** una misma orden.
- Una salida vinculada se bloquea mientras el lavado no esté pagado y entregado.

### Abonados
- Planes completos **24 horas** semanales, quincenales o mensuales, con versiones históricas.
- Cada contrato comienza **pendiente y sin cobertura**. Pasa a activo exclusivamente después de registrar un cobro en una caja abierta.
- Cobertura por patente/tipo, desde la activación hasta el vencimiento; sin retroactividad y sin solapamientos vigentes.
- Al salir, solo se descuenta el estacionamiento cuando el abono cubre **todo** el período de la estadía. Si vence durante esa estadía, se aplica la tarifa normal; la facturación parcial por franjas se diseñará posteriormente.
- Renovación por nuevo contrato al terminar la vigencia anterior; todavía no admite flotas, plaza reservada ni planes nocturnos.

### Caja y reportes
- Un solo cobro complementario por orden/contrato, con clave de operación idempotente.
- Los ingresos en efectivo de ambos módulos suman al efectivo esperado, y los electrónicos solo al total de otros medios.
- Recaudación Excel/PDF registra origen Estacionamiento/Lavadero/Abono, sin mezclar conceptos.
- Migración de esquema v6 con respaldo SQLite previo. Sin borrar operaciones anteriores.

**Limitaciones de v0.6:** los medios externos siguen siendo una verificación **declarada** por el empleado; aún faltan devoluciones de cobros complementarios, renovaciones prorrateadas, facturación fiscal, eventos antifraude y pruebas reales de impresora. No utilizar con dinero de clientes.

### Validación sin artefactos

Se mantiene una sola tarea de validación manual en GitHub Actions. No produce ni sube paquetes. Las versiones descargables se reanudarán únicamente cuando lleguemos a un hito importante.

## Funcionalidad implementada (rama de trabajo)

- Interfaz nativa WinUI 3: inicio, nuevo ingreso, salida de prueba, vehículos presentes y secciones preparadas.
- SQLite local: patentes normalizadas, ticket secuencial y bloqueo de duplicados activos.
- Búsqueda por patente o número de ticket.
- Cálculo tarifario **ficticio**, en centavos enteros, con cortesía, fracciones y tope diario.
- Salida transaccional con importe **simulado**. No se registra dinero recibido.
- Respaldo manual consistente con la API de SQLite.
- **Novedad v0.2:** Enter avanza entre patente, categoría, sector y botón Guardar. F2 (ingreso), F4 (salida), F8 (presentes) y F10 (guardar desde ingreso).
- Impresión de ticket de ingreso en impresora Windows con QR real y número único; seleccioná una impresora y ancho 58/80 mm en Configuración.
- Búsqueda de salida por ticket, patente o contenido del QR leído con escáner USB tipo teclado.
- Reimpresión por número de ticket sin duplicar el ingreso, más registro de intentos y fallos de impresión.
- Migración conservadora: incorpora tokens QR a estadías anteriores y **no borra la base existente**.
- Pruebas automatizadas para tarifas, ingreso duplicado, salida y reingreso.

## Operación nueva v0.3 — caja y tarifas

Esta versión agrega un primer circuito de **cobro en efectivo confirmado manualmente**, no integración con bancos ni una habilitación fiscal.

1. Abrí **Tarifas** y definí los importes para auto, moto y camioneta (montos en ARS, con fracción y tope de 24 h). Al guardar se crea una nueva versión. La tarifa asignada en el ingreso no cambia durante la estadía.
2. Entrá a **Caja**, indicá un operador y el fondo para vuelto y abrí un turno. Solo se admite una caja local abierta por vez.
3. En **Salida y cobro** buscá por ticket, patente o QR. El programa calcula el saldo a partir de la tarifa de ingreso, lo cotiza por 2 minutos y exige volver a calcular si cambia el importe.
4. Ingresá cuánto efectivo recibiste, comprobá el vuelto y presioná **Confirmar efectivo recibido y finalizar salida**. El cobro, el movimiento de efectivo y la salida se guardan juntos en SQLite.
5. En **Caja** consultá la recaudación y registrá el efectivo contado para cerrar el turno. Se guarda esperado, contado y diferencia.

**No usar todavía con dinero de clientes:** faltan usuarios autenticados, control de permisos, devoluciones, retiro de efectivo, conciliación y pruebas físicas del flujo. No contempla tarjetas ni transferencias. Una entrada de efectivo registrada presupone que el cajero realmente recibió el dinero.

El esquema se migra automáticamente, con respaldo consistente previo a la migración. Los cobros simulados de v0.1/v0.2 **no se incorporan** a caja. Cada estadía recibe una versión de tarifa propia.

## Novedades de Aparcar v0.4

**No apta para dinero real sin las pruebas y controles pendientes.** Esta versión funciona localmente y todavía no autentica a los usuarios ni verifica pagos mediante una API externa.

- **Medios de pago:** efectivo, débito, crédito, transferencia y QR. Para los cuatro métodos externos exige marcar la verificación realizada fuera de Aparcar e ingresar una referencia del proveedor. *No consulta bancos ni pasarelas ni garantiza acreditación.*
- **Caja separada:** el efectivo esperado nunca incluye cobros electrónicos. Permite depósitos y retiros con fecha, operador y motivo; los arqueos descuentan retiros y devoluciones.
- **Devolución:** únicamente *completa y en efectivo*, vinculada a un cobro existente. Es única por cobro y exige PIN local de supervisor creado sin valor predeterminado. Ese PIN es un control provisional, no sustituye la autenticación y permisos de personal.
- **Reportes:** período desde/hasta, cobros brutos, devolución por fecha de reintegro, efectivo neto, medios externos, detalle de tickets y Excel real `.xlsx` en Documentos/Aparcar/Reportes. El PDF todavía no está implementado.
- **Respaldo antes de migrar:** nueva estructura de SQLite en `EsquemaVersion=4`, conserva estadías, tarifas y cobros v0.3. Las operaciones anteriores se reconocen como efectivo.
- **Identificador único de cobro:** si se reintenta una operación confirmada, se recupera el registro previo y no se vuelve a cobrar.

### Circuito recomendado de pruebas

1. Abrí Caja y registrá un turno.
2. Registrá un ingreso y buscá la salida.
3. Cambiá el medio a transferencia, cargá una referencia de prueba y marcá el comprobante como verificado. Confirmá, solo con datos ficticios.
4. Comprobá en Caja que el saldo de efectivo **no** aumente por la transferencia.
5. Simulá un cobro en efectivo, un depósito y un retiro; compará el saldo esperado.
6. Configurá un PIN local de supervisor y probá una devolución completa de ese cobro en efectivo.
7. Abrí Reportes, consultá un período y exportá el Excel real.
8. Cerrá el turno y confirmá el arqueo. Reabrí la aplicación y comprobá la persistencia.

**Restricciones pendientes:** roles y autenticación, pasarelas reales, conciliación bancaria, reembolsos externos, devolución parcial, reportes PDF, ventas de lavadero/abonos, terminales multicaja y auditoría anti-manipulación.

## Requisitos

- Windows 10/11 x64.
- Visual Studio 2026 con **WinUI application development**.
- SDK .NET 10 o superior; comprobar con `dotnet --version`.
- Conexión a Internet **durante la primera preparación** para plantillas y NuGet.

Documentación de Microsoft: https://learn.microsoft.com/windows/apps/get-started/start-here

## Primera compilación

Desde PowerShell en la carpeta del repositorio:

```powershell
git clone https://github.com/matuky2005/aparcar.git
cd aparcar
git switch feature/base-winui-sqlite-v0.1
powershell -ExecutionPolicy Bypass -File .\scripts\Preparar-Aparcar.ps1
```

Después abrí `Aparcar_VisualStudio\Aparcar.sln`, seleccioná la plataforma **x64** y ejecutá el proyecto `Aparcar.Desktop` con F5.

El script crea la solución a partir de la **plantilla oficial WinUI 3 empaquetada** (single-project MSIX), incorpora los archivos fuente de este repositorio, instala la dependencia de SQLite, ejecuta las pruebas y compila. Detiene la ejecución si ya existe la carpeta `Aparcar_VisualStudio`, para no sobrescribir trabajo previo.

GitHub Actions genera artefactos descargables de v0.3: versión portable y MSIX firmado con certificado TEMPORAL de pruebas. El MSIX **no está certificado por Microsoft Store**; su identidad de prueba puede variar. Para probar sin instalar el certificado, usá la versión portable.

## Dónde están los datos

Base de prueba:
`%USERPROFILE%\Aparcar\Datos\aparcar.db`

Respaldo manual:
`Documentos\Aparcar\Respaldos`

**No borres esas carpetas si querés conservar las pruebas.** Esta ruta es MONOUSUARIO: la ubicación definitiva compartida y el procedimiento de recuperación serán validados antes de ofrecer una versión comercial.

## Estructura del repositorio

- `src/Aparcar.Domain`: modelo y tarifas de demostración.
- `src/Aparcar.Infrastructure`: SQLite y transacciones.
- `src/Aparcar.Desktop`: XAML y eventos de WinUI.
- `tests/Aparcar.Tests`: pruebas automatizadas.
- `scripts/Preparar-Aparcar.ps1`: generación de solución Windows.
- `docs/ARQUITECTURA.md`: decisiones técnicas.

## Pendientes obligatorios antes de producción

Caja y cierre de turno, tarifas configurables/versionadas, identidad de usuario y permisos, cobros verificables, idempotencia de operaciones externas, ticket QR e impresión, recuperación/restauración, auditoría, empaquetado firmado, controles multicaja y pruebas reales en Windows.

**No usar los valores simulados para recibir pagos de clientes.**

## Probar impresión real (v0.2)

1. Instalá el controlador oficial de la impresora en Windows y verificá que imprima una página de prueba desde Windows.
2. En Aparcar abrí **Configuración**, elegí la impresora y papel de 58 u 80 mm; presioná **Guardar configuración**.
3. Abrí **Nuevo ingreso** con F2. Completá patente → Enter → tipo → Enter → sector → Enter → Guardar o F10.
4. Si activaste la impresión automática, Aparcar enviará el ticket a la cola de Windows **después de guardar** la estadía. Si falla la impresión, se conserva el ingreso.
5. En **Configuración**, ingresá su número de ticket y tocá **Reimprimir ingreso** si fuera necesario.
6. Escaneá el QR con un lector USB compatible con entrada de teclado desde **Salida (prueba)**; también podés buscar por número o patente.

**Límites actuales:** compatible con impresoras que aceptan salida GDI mediante controlador Windows, no con cualquier dispositivo ESC/POS directo; el envío al controlador no confirma que haya salido papel. Se debe validar el ancho de papel y los márgenes en el equipo real. Los datos siguen en la ruta externa; no reemplaces ni borres `aparcar.db` al actualizar.

