# Instalar Aparcar v0.24.0 (pruebas Windows x64)

## Estado y advertencias
Este instalador **es una versión técnica de pruebas**. La facturación ARCA continúa SOLO en homologación, sin validez fiscal comercial. Los cobros y el flujo operativo no han superado todavía una prueba completa de caja y de impresoras reales. No utilizar en producción ni con dinero real hasta validar el programa.

## Generación del EXE
La rama `feature/base-winui-sqlite-v0.1` tiene el workflow GitHub Actions `Aparcar - Instalador Windows x64`, que hace:
1. Windows runner + .NET 10.
2. `./scripts/Preparar-Aparcar.ps1`: prepara proyectos a partir del código, compila y **ejecuta tests xUnit**. Si una prueba falla no continúa la ejecución.
3. `./scripts/Crear-Instalador-Aparcar.ps1`: publica `Aparcar.Desktop` en Release para `win-x64`, sin MSIX y con .NET + Windows App SDK autocontenidos.
4. Inno Setup 6 empaqueta todos los ficheros de `dotnet publish` como **`Aparcar-Setup-0.24.0-x64.exe`**.
5. Publica el instalador como **artefacto** en el run de GitHub Actions, solo cuando todas las etapas anteriores finalizan sin error.

No se generan certificados ni instaladores MSIX en este paso. El EXE no está firmado digitalmente: Windows Defender SmartScreen puede mostrar advertencia de editor no reconocido. Antes de instalar, comprobar que procede del repositorio oficial y comparar el SHA256 de GitHub Actions.

Si el workflow termina en error, **no existe un instalador validado**; inspeccionar los logs y corregir la compilación o las pruebas.

## Descarga e instalación
1. Abrir [Actions del repositorio](https://github.com/matuky2005/aparcar/actions), elegir `Aparcar - Instalador Windows x64`.
2. Abrir la ejecución con resultado verde y descargar el artefacto `Aparcar-Setup-0.24.0-x64`.
3. Descomprimir el ZIP que entrega GitHub; ejecutar `Aparcar-Setup-0.24.0-x64.exe` en Windows 10/11 x64.
4. Usar un equipo **de pruebas** sin cobros de clientes. Configurar administrador inicial, sector, tarifas, impresora y turno de caja.

El programa se instala por defecto en `%LOCALAPPDATA%\Programs\Aparcar`, para el usuario actual y sin requerir permisos de administrador. La aplicación guarda su SQLite en `%USERPROFILE%\Aparcar\Datos\aparcar.db` y crea respaldos en `Documentos\Aparcar\Respaldos`. Estos datos **no están dentro** del directorio del instalador y no se incluyen en el EXE. Actualizar o desinstalar **no debe** eliminarlos.

**Importante:** no desinstalar sin contar con un respaldo válido; el instalador no es un sustituto del plan de recuperación de datos ni de una prueba de restauración.

## Creación en PC con Windows
Se requiere .NET SDK 10, internet para paquetes NuGet y plantillas WinUI, y Inno Setup 6:
```powershell
git clone -b feature/base-winui-sqlite-v0.1 https://github.com/matuky2005/aparcar.git
cd aparcar
powershell -ExecutionPolicy Bypass -File .\scripts\Preparar-Aparcar.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Crear-Instalador-Aparcar.ps1
```
El script original de preparación se niega a sobrescribir `Aparcar_VisualStudio` si ya existe, para proteger cambios locales.

## Limitaciones
- El instalador es un **EXE de pruebas sin firma**, no el MSIX destinado a Microsoft Store.
- WinUI 3 unpackaged no tiene identidad MSIX, por lo que no ofrece actualizaciones Store.
- El SQL local es mono-PC; no compartir la base SQLite por SMB ni operar varias cajas simultáneamente.
- Hay que probar instalación limpia, actualización sobre datos existentes, desinstalación/reinstalación, impresión 58/80 mm, Ticket X, seguridad por roles, recuperación de recibos, cierres de caja y restauración antes de cualquier uso comercial.
