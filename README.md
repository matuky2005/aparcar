# Aparcar

Sistema de estacionamientos para Windows, con diseño para incorporar lavadero, abonados y varias cajas.

> **Estado: versión técnica 0.3 — NO apta para cobros reales ni para producción.**

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

