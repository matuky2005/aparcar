# Aparcar v0.16 — Perfiles fiscales reutilizables y versionados

**Estado:** desarrollo técnico. Exclusivamente homologación ARCA; sin emisión productiva, sin cobros comerciales habilitados y sin generación de instaladores.

## Modelo de datos

Se implementó la migración SQLite **esquema 14**, con respaldo automático previo:

- `VersionesPerfilesFiscales`: `CuitEmisor`, `Version`, `DatosJson`, `HuellaSha256`, `CreadoUtc`. Clave compuesta `(CuitEmisor, Version)`. Nunca se reemplaza una versión histórica desde las operaciones de la aplicación.
- `PerfilesFiscalesActuales`: una fila por CUIT y puntero `VersionActual`, enlazado por clave foránea compuesta a la versión histórica.
- Se verifica SHA-256 antes de leer un perfil, para detectar corrupciones o modificaciones no autorizadas. **SHA-256 sin clave no evita una alteración maliciosa por alguien con pleno control del archivo SQLite**.
- Guardar exactamente los mismos datos devuelve la versión vigente sin duplicarla. Una modificación registra otra versión y cambia el puntero vigente en la misma transacción, con auditoría.
- Las facturas anteriores conservan su captura fiscal inmutable; no se actualizan al cambiar un perfil.

## Operación desde WinUI

En configuración fiscal:

1. El administrador carga el CUIT emisor y completa los datos comerciales (razón social, domicilio, nombre de fantasía, IVA, IIBB, fecha de inicio).
2. Puede guardar los datos como perfil o crear una versión nueva. Si nada cambió, la versión se mantiene.
3. Puede buscar perfiles, elegir uno y aplicar los datos al formulario; también ver el historial con fecha/hora de las versiones.
4. Cuando captura la identidad comercial de una solicitud fiscal `Preparada`, Aparcar conserva el perfil exacto utilizado mediante `VersionPerfilEmisor`. La persistencia comprueba que el JSON del perfil de esa versión sea **idéntico** a la identidad del emisor seleccionada; si alguien modifica un campo después de aplicar, se bloquea hasta guardar una nueva versión o reaplicar el perfil.
5. El perfil debe coincidir con el CUIT del emisor y de la solicitud. No se puede cruzar un perfil entre contribuyentes.
6. Se puede seguir capturando manualmente la identidad comercial como en v0.15; en tal caso no hay versión vinculada y el documento conserva de todos modos el snapshot íntegro y su hash.

La copia **NO FISCAL** de homologación indica la versión del perfil utilizada, sin QR comercial. Se preserva la validación de CAE exclusivamente de prueba.

## Seguridad y siguientes pasos

- Los perfiles son información declarada manualmente y **no implican consulta al padrón de ARCA**; los datos fiscales deben ser validados antes de producción.
- Sólo un administrador autorizado puede crear, modificar o recuperar perfiles; no se reutilizan PFX, token ni contraseña.
- Se amplía el verificador de respaldos para aceptar el esquema 14.
- **Pendiente**: ejecución manual de CI WinUI y todas las pruebas nuevas, backup/restore real en Windows, interfaz para comparar versiones campo por campo, prevención multi-terminal y verificación fiscal productiva.
- No activar ni integrar el QR productivo con un simple indicador de ambiente: primero exigir CAE realmente autorizado en producción y revisión legal de comprobantes.

No fusionar a `main` ni publicar MSIX hasta superar las verificaciones de la etapa.
