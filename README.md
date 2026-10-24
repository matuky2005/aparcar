# Aparcar

Sistema de estacionamientos para Windows, con diseño para incorporar lavadero, abonados y varias cajas.

> **Estado: código de desarrollo v0.5 — NO apto para uso comercial ni cobros reales.**

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

