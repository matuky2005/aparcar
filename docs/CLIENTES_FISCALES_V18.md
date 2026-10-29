# Aparcar v0.18 — Cartera de clientes fiscales

**Estado: código en la rama de desarrollo, no habilitado para comercializar ni facturar en ARCA producción.**

## Objetivo

Guardar y reutilizar datos de clientes identificados por CUIT, CUIL o DNI; usar la Constancia de Inscripción ARCA en homologación como ayuda para completar nombres y domicilios, sin confundirla con una validación automática del tratamiento impositivo. No crear un registro para cada consumidor final anónimo.

## Persistencia

Esquema SQLite **15** (versión de aplicación v0.18), migración con respaldo previo:

- `ClientesFiscales`: `Id`, `CuitEmisor`, documento tipo/número, `NombreBusqueda`, `VersionActual`, `Activo`, fechas. Se impone unicidad de **CUIT emisor + tipo de documento + número**.
- `VersionesClientesFiscales`: un JSON por versión histórica, hash SHA-256, origen `Manual` o `ConstanciaHomologacion`, fecha de consulta (cuando corresponde) y fecha de registro. No se reescriben versiones anteriores.
- Se registra un nuevo número de versión únicamente cuando cambian los campos del receptor. Guardar datos idénticos no duplica registros y reutiliza el ID.
- Desactivar un cliente lo oculta de la cartera, pero conserva snapshots de comprobantes de homologación. Volver a guardar el mismo documento lo reactiva.
- Consulta y guardado requieren `PermisosAparcar.Configuracion`; auditoría guarda el ID del registro y versión, **no nombre, domicilio, documento ni correo electrónico**.
- El control SHA-256 puede detectar alteraciones accidentales; **NO cifra** los datos ni impide que un atacante con acceso a la base rehaga el hash. Restringir archivos y respaldos; definir política de retención/eliminación antes de publicar.

## Integración con operaciones fiscales

- En Configuración fiscal, el operador autorizado ingresa un CUIT emisor, busca por documento o nombre y elige un cliente guardado.
- `Usar cliente` rellena documento, nombre, domicilio y correo. **No reemplaza automáticamente la condición IVA oficial**: solo la preselecciona si está presente en un catálogo reciente consultado en ARCA para el mismo emisor/clase. Si no, debe consultarse el catálogo vigente.
- `Guardar cliente / nueva versión` conserva un receptor identificado, una vez revisados los datos. Si proviene de Constancia de Inscripción en esta sesión, el origen se marca como tal solo cuando **CUIT, razón social y domicilio** siguen coincidiendo exactamente con el resultado consultado. Cualquier otro caso se registra `Manual`.
- Las facturas en homologación continúan guardando `ReceptorDocumentoFiscal` completo dentro de `DatosDocumentosFiscalesPrueba`, además de poder añadir `ClienteFiscalId` y `VersionClienteFiscal`. La misma transacción comprueba que el emisor, documento, hash y datos son exactamente iguales a la versión seleccionada. Si cambiaron los campos, se bloquea y exige guardar nueva versión o aplicar nuevamente.
- El PDF de prueba indica la referencia histórica del cliente y conserva nombre, documento, domicilio y condición IVA originales. No hay emisión fiscal productiva ni QR de producción.
- `Consumidor Final` sin identificar permanece como opción de facturación por defecto según v0.17, pero **no se guarda como cliente 99/0**. Si se carga un cliente identificado, prevalece y queda marcada la opción de identificarlo a solicitud.
- Los clientes se almacenan **por emisor**, sin mezclarlos entre empresas que operen en la misma base. No hay sincronización multicaja: sigue siendo la edición local.

## Pruebas agregadas

`ClientesFiscalesV18Tests.cs`: guarda/lee sin duplicar, versión nueva ante cambios, aislamiento entre emisores, búsqueda, snapshot fiscal histórico intacto, rechazo de versiones falsas o receptor editado, desactivación/reactivación, exclusión de consumidor final anónimo y detección de hash modificado. Todavía deben ejecutarse junto al conjunto existente en Windows.

## Requisitos antes de uso comercial

Homologación ARCA real con CUIT/certificado autorizado; migración y backup/restauración validados; permisos granulares del operador; cifrado de información personal en reposo y respaldos; política de privacidad, retención y eliminación; validación dinámica del estado impositivo e identificación; pruebas físicas de impresoras y pruebas de actualización MSIX sin pérdida de datos.

**Política de CI:** exclusivamente disparo manual, sin ZIP/MSIX ni artefactos de build. PR borrador; no fusionar todavía.
