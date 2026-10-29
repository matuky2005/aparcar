# Arquitectura de Aparcar

## Objetivo de v0.1
Demostrar un circuito **local**, en una computadora: ingreso, búsqueda de estadía activa, cálculo ilustrativo, salida y respaldo SQLite. No hay caja real ni validación de pagos.

## Separación de módulos
- `Aparcar.Desktop`: WinUI 3 (interfaz, navegación).
- `Aparcar.Domain`: entidades y cálculo de ejemplo, sin depender de Windows ni de SQLite.
- `Aparcar.Infrastructure`: consultas y transacciones SQLite.

## Preparación multicaja
En una fase futura, los casos de uso y las transacciones se ejecutarán centralmente mediante una API + PostgreSQL. Nunca compartir directamente el archivo SQLite por una carpeta de red.

## Persistencia
Versión monousuario: `%USERPROFILE%\Aparcar\Datos\aparcar.db`. Es deliberadamente externa a la carpeta de instalación MSIX, pero **no es aún una solución multiusuario**. Antes del lanzamiento se validarán los permisos, rutas persistentes, pruebas de actualización y respaldos automáticos.

## Invariantes v0.1
1. Una patente normalizada solo puede tener una estadía abierta.
2. El ticket se crea junto con la estadía.
3. La salida y su importe de prueba se confirman en una transacción.
4. Los importes simulados no se registran como cobros reales.
5. La copia de respaldo utiliza la API de SQLite para una captura consistente.

## Pendientes para uso comercial
Tarifas versionadas, caja/turnos, pagos reales conciliados, roles/permisos, dispositivos, tickets QR, auditoría, migraciones y pruebas Windows/MSIX. No publicar esta versión como producto comercial.
