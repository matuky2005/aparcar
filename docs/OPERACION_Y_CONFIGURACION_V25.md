# Aparcar v0.25: operación, impresora y configuración

Versión de pruebas. Mantener caja, facturas y certificados de producción fuera del uso comercial hasta completar la validación integral, revisión fiscal y homologación real.

## Interfaz

- Pestañas en Inicio, Salida, Tarifas, Caja, Lavadero, Abonados y Configuración. El historial de recibos se encuentra en Salida y cobro > Recibos. La configuración tiene sectores, impresora, talonarios, certificados, facturación de prueba, solicitudes, control documental y respaldos.
- F2 y F4 vuelven a enfocar ingreso y salida, incluso estando ya en la pantalla. Al ingresar, Enter avanza desde patente a tipo y luego a guardar si hay un solo sector, o a sector si hay más de uno. Tab permite cargar una plaza opcional. F10 registra.
- En Inicio, una coincidencia prepara la salida sin cobrar; con varias, se selecciona una y Enter prepara. En Salida, buscar cotiza sin cobrar. Se propone efectivo exacto y se selecciona el importe para poder cambiarlo. El cajero debe confirmar expresamente el cobro recibido; pagos electrónicos conservan referencia y verificación manual.
- F5 y F10 solo operan sobre la pestaña de operación visible. No crean otro cobro al imprimir o recuperar recibos.

## Impresora

1. En Configuración > Impresora, actualizar la lista de impresoras de Windows.
2. Elegir el dispositivo instalado y papel 58 u 80 mm. Hacer la página de prueba y comprobar que salga físicamente; se envía al controlador GDI de Windows, no mediante ESC/POS ni diálogo de impresión.
3. Elegir impresión automática de ingreso y recibo por separado y guardar. Las preferencias de v0.24 se conservan como valores iniciales. Los cambios tienen efecto al guardar.
4. Si falla un envío, el ingreso o cobro ya guardado sigue registrado. Consultar su número y usar la recuperación autorizada en vez de repetir la operación. Un trabajo aceptado por el controlador no asegura papel impreso.

La causa de fallas de impresión en una PC concreta requiere probar su modelo, conexión, controlador, papel y cola de Windows. Las impresoras sin controlador GDI compatible no están soportadas por este camino.

## Talonarios

Configuración > Talonarios permite crear X, cambiar nombre, suspender/reactivar, seleccionar el predeterminado e imprimir o recuperar X existentes. La secuencia y los documentos históricos se conservan. Un X suspendido no emite nuevos números. El X es no fiscal y no sustituye factura.

El catálogo fiscal guarda por separado ambiente, CUIT, punto de venta, clase A/B/C, nombre y estado local. Guardar allí no crea ni habilita un punto de venta en ARCA, no consulta ni cambia su numeración y no activa producción. Un registro habilitado de homologación puede cargar los parámetros de consulta existentes; la numeración se consulta en ARCA. El cambio de identidad del registro crea otro, no borra el anterior.

## Certificados ARCA

En Configuración > Certificados ARCA, elegir el ambiente y generar CSR con CUIT titular, razón social, nombre de sistema y contraseña. La clave RSA de 2048 bits queda cifrada en PKCS#8 con AES-256 en Documentos/Aparcar/ARCA/{Homologacion o Produccion}; el CSR se presenta a ARCA. La contraseña no se almacena; conservar clave y contraseña de forma segura.

ARCA emite el certificado, no Aparcar. Homologación se gestiona en WSASS; producción, en Administrador de Certificados Digitales. Asociar los servicios correspondientes como indica ARCA. El certificado descargado debe pertenecer a la solicitud y al ambiente adecuado. Para homologación el CUIT del CSR debe corresponder al titular conectado a WSASS; un CUIT representado puede requerir delegación.

Con la clave y el certificado emitido por ARCA, Aparcar verifica que ambas claves correspondan y construye un PFX protegido, en la carpeta del ambiente elegido. El selector de ambiente es una declaración del usuario; no verifica por sí solo el emisor ni la autorización en ARCA. La clave se descifra solamente en memoria. Copiar los archivos cifrados a un respaldo seguro: el respaldo SQLite no los incluye. Nunca incluir claves ni contraseñas en el repositorio.

Referencia: https://www.arca.gob.ar/ws/documentacion/wsaa.asp y https://www.arca.gob.ar/ws/WSASS/html/generarcsr.html.

## Validación pendiente

Compilación y pruebas Windows en GitHub Actions, inicio real de la aplicación y prueba de teclado, instalación y actualización, impresora física, creación de CSR/PFX con certificado real de cada ambiente, autorización WSAA y homologación WSFE. Producción fiscal sigue deshabilitada.
