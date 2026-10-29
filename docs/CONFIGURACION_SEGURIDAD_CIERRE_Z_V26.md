# Aparcar v0.26 — configuración, respaldos y cierres Z

## Configuración centralizada

La sección Configuración reúne ahora tarifas generales y sectoriales, sectores y plazas, impresoras, talonarios X, puntos de venta fiscales, certificados ARCA, perfiles fiscales, clientes y seguridad. Las tarifas se guardan como nuevas versiones y no modifican estadías ya iniciadas.

Los catálogos de servicios de lavadero y planes de abonados se abren desde la pestaña Maestros. La operación diaria conserva únicamente los formularios necesarios para ingresar, cobrar y consultar.

## Respaldo de seguridad

Al iniciar Aparcar se crea un respaldo SQLite consistente en `Documentos/Aparcar/Respaldos`, salvo que el administrador desactive expresamente esa política. También se conserva el respaldo manual y la restauración diferida con verificación de `quick_check`, claves foráneas y SHA-256.

Un respaldo no contiene certificados ni claves privadas almacenadas por el sistema fuera de las rutas que el administrador elija. Las contraseñas de certificados no se guardan.

## Cierre Z

Al cerrar un turno se genera dentro de la misma transacción un cierre Z operativo, correlativo e inmutable. Incluye operador, apertura, cierre, efectivo recaudado, esperado, contado y diferencia. El número no se vuelve a utilizar aunque falle la impresión de un reporte.

El cierre Z es un control interno de caja: no es una declaración fiscal, no genera CAE y no reemplaza los comprobantes de ARCA.
