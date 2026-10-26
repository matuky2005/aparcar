# Aparcar

Sistema de estacionamientos para Windows, con diseño para incorporar lavadero, abonados y varias cajas.

> **Estado: Aparcar v0.24 — instalador EXE de pruebas en preparación. NO apto para uso comercial ni cobros reales.**

### Instalador de pruebas Windows x64

El instalador `Aparcar-Setup-0.24.0-x64.exe` se genera en GitHub Actions **solo si** pasan la compilación WinUI 3, los tests y la publicación autocontenida. La descarga aparece como artefacto de un run exitoso del workflow `Aparcar - Instalador Windows x64`. No es MSIX, no está firmado y ARCA sigue exclusivamente en homologación. [Instrucciones de instalación y preservación de datos](docs/INSTALADOR_WINDOWS_V024.md).


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

## Etapa v0.24: auditoría de integridad y reimpresiones X

- Se corrigió un problema de la v0.23: aunque un cobro tenía un único número X, se podían registrar varios intentos de impresión **ORIGINAL** para el mismo ticket.
- Nueva migración **SQLite esquema 18** con respaldo previo, posterior al esquema 17 de recibos. El trigger `TR_TicketX_OriginalUnico` bloquea nuevos intentos ORIGINAL repetidos, sin borrar históricas que pudieran existir.
- Se agrega validación en `RegistrarIntentoImpresionX`, con error explícito orientado a recuperar una **COPIA autorizada**, incluso cuando el intento anterior de impresión está pendiente o falló.
- Nuevas pruebas para estados `Pendiente`, `Enviado`, `Error`, consultas, recuperación de históricos y acceso SQL directo.
- La revisión estática comprobó **105 manejadores de eventos XAML**, migraciones, permisos y que el CI sigue manual y sin artefactos. Una prueba SQLite aislada también confirmó el bloqueo y que las copias siguen permitidas.
- **Todavía no se compilaron ni ejecutaron tests C# en Windows** de v0.24. [Documentación de auditoría](docs/AUDITORIA_INTEGRIDAD_X_V24.md). No se generaron instaladores ni se hizo merge a `main`.

## Etapa v0.23: opción Ticket X desde cada cobro

- En **Salida y cobro**, **Lavadero** y **Abonados** se agrega una casilla **«Emitir ticket X NO FISCAL»** desmarcada por defecto.
- El administrador configura un talonario X predeterminado desde la sección de talonarios. Se guarda como preferencia con auditoría, sin reiniciar el contador y sin crear un punto de venta fiscal.
- Si se marca X, el programa verifica ANTES de cobrar los permisos del operador, la existencia del talonario habilitado y la impresora Windows configurada.
- La transacción del cobro se confirma primero; únicamente después se emite el ticket X referenciado por `Origen/CobroId`. El pago nunca se repite por errores de impresión y el mismo cobro no puede obtener otro X.
- En estacionamiento, imprimir X **sustituye solo la impresión automática del recibo operativo**, no sustituye la factura A/B/C exigible ni cancela pendientes ARCA.
- Si falla el envío a impresora tras grabar el X, se conserva el número, se informa **COBRO CONFIRMADO** y se utiliza **copia autorizada** desde el historial para recuperarlo.
- No se crean nuevas tablas: permanece **SQLite 16**. Pruebas unitarias agregadas para la preferencia X y sus validaciones; compilación y ejecución reales en Windows pendientes.
- [Documentación del flujo X en caja](docs/TICKET_X_COBRO_V23.md). Sin ZIP/MSIX ni artefactos, PR todavía en borrador.

## Etapa v0.19: talonarios X no fiscales para operaciones cobradas

- Talonarios X propios por código de cuatro dígitos y numeración de ocho dígitos consecutivos. **No se registran como puntos de venta fiscales** y no generan CAE.
- Nueva migración **SQLite 16** con respaldo previo y nuevas tablas `TalonariosX`, `TicketsX` e `ImpresionesX`. Se amplía el verificador de restauración al esquema 16.
- Emisión X solo sobre **cobros confirmados positivos y no devueltos** de Estacionamiento, Lavadero o Abono. Una referencia de cobro admite un único X y reintentos idénticos devuelven el existente. Todo se guarda antes de imprimir para evitar perder números.
- Impresión térmica de 58/80 mm con indicación destacada **DOCUMENTO NO VALIDO COMO FACTURA**, número, origen, importe, medio y fecha. No emite QR fiscal ni sustituye la documentación fiscal correspondiente.
- Reimpresión identificada como copia, restringida por permiso y con motivo; se registran intentos y resultados de la impresora. Un X histórico cuyo cobro fue devuelto muestra una advertencia en copia.
- **No se crean segundos cobros ni movimientos de caja por imprimir X**. Se mantienen los pendientes de facturación fiscal independientes.
- Nuevas pruebas de numeración, cobros, duplicados, caja, reimpresión y devoluciones. **Aún requieren compilación/ejecución en Windows.**
- Documento técnico: [docs/TALONARIOS_X_V19.md](docs/TALONARIOS_X_V19.md). PR continúa en borrador y sin instaladores.

## Etapa v0.18: cartera de clientes fiscales persistente

- Se añade una cartera por **CUIT emisor y documento del receptor** (CUIT/CUIL/DNI). Consumidor Final anónimo 99/0 no se guarda como cliente.
- Migración SQLite **esquema 15** con respaldo antes del cambio. `ClientesFiscales` mantiene una versión vigente y `VersionesClientesFiscales` conserva cada versión histórica como JSON y hash.
- Desde la pantalla fiscal se puede buscar por nombre o documento, aplicar los datos existentes, guardar cambios con versión nueva, consultar versiones y desactivar registros sin alterar comprobantes anteriores.
- La consulta de Constancia ARCA v0.17 puede usarse para obtener nombre y domicilio. Guardar en cartera es **una acción explícita**, y únicamente se marca origen `ConstanciaHomologacion` si datos y CUIT coinciden con la consulta efectuada. No se asigna IVA sin catálogo WSFE vigente.
- La solicitud fiscal en homologación conserva su snapshot, ahora con referencia opcional al ID y versión del cliente. La transacción rechaza referencias de otro emisor o datos modificados que no correspondan a esa versión.
- Se suman pruebas de unicidad, versionado, aislamiento de emisores, inmutabilidad, baja/reactivación y protección SHA-256. **Compilación y ejecución de tests en Windows pendientes.**
- Más detalles: [docs/CLIENTES_FISCALES_V18.md](docs/CLIENTES_FISCALES_V18.md). No se generan MSIX/ZIP y no se fusiona el PR.

## Etapa v0.17: constancia de inscripción ARCA y Consumidor Final

- Se integra en homologación `ws_sr_constancia_inscripcion` (`getPersona_v2`, versión 4.1), usando **WSAA con ticket independiente para el servicio de padrón**. Consulta datos reales del CUIT si la credencial está habilitada; no es una consulta pública ni se ha probado con certificado autorizado.
- La UI permite consultar constancia del **receptor** identificado por CUIT para obtener nombre/razón social y domicilio fiscal. También puede consultar constancia del **emisor** y completar campos del perfil. El IVA, IIBB, domicilio comercial y demás información requieren revisión; no se infieren IDs WSFE de un padrón.
- Preferencia opcional de **Consumidor Final por defecto, por CUIT emisor**, guardada en SQLite con auditoría; puede desactivarse. Al consultar condiciones IVA vigentes para Factura B/C, si hay un cobro seleccionado y no está identificado el receptor, se sugiere `DocTipo 99 / DocNro 0` solo si el catálogo oficial contiene una condición única de Consumidor Final.
- Bloqueo de consumidor final anónimo en operaciones de **$10.000.000 o más** conforme reglas vigentes en octubre de 2026; también se respeta la solicitud explícita de identificar al cliente y se evita sobrescribir un DNI/CUIT previamente ingresado.
- Se agregaron pruebas de consulta SOAP simulada de padrón, respuesta inválida, reglas de identificación, actualización de preferencia y estado del emisor. **No se han ejecutado las nuevas pruebas ni la compilación WinUI todavía**.
- Documentación de la etapa: [docs/ARCA_CONSTANCIA_CF_V17.md](docs/ARCA_CONSTANCIA_CF_V17.md). El proyecto permanece exclusivamente en homologación y sin instaladores.

## Etapa v0.16: perfiles fiscales por CUIT con historial de versiones

- Nueva migración SQLite a **esquema 14**, siempre precedida por respaldo. Se crea una tabla de versiones inmutables por CUIT y otra tabla con la referencia a la versión vigente.
- Un administrador puede guardar los datos del emisor, reutilizarlos en nuevas solicitudes, buscar perfiles y consultar un historial. Los cambios generan versiones nuevas; guardar datos sin cambios no crea duplicados.
- El snapshot fiscal de la solicitud preparada puede referenciar una versión específica del perfil. Antes de guardarla se comprueba que **el CUIT, versión y contenido del perfil** coincidan exactamente con los datos del emisor. Si se editaron los campos sin versionarlos, se bloquea la operación.
- Los comprobantes históricos conservan sus datos tal como fueron registrados; actualizar el perfil no los modifica. La copia de homologación muestra el número de versión utilizado, pero **no es una factura comercial**.
- Se extiende la verificación de respaldos hasta el esquema 14. Nuevos tests cubren historial, versiones duplicadas, snapshot histórico, discrepancias y manipulación del hash.
- El desarrollo continúa en el PR borrador sin MSIX ni artefactos. **Compilación y tests v0.16 pendientes de ejecutar en Windows.**
- [Documentación completa de perfiles](docs/PERFILES_FISCALES_V16.md).

## Etapa v0.15: identidad comercial y QR ARCA preparado, sin activar producción

- Identidad comercial del emisor y receptor asociada a cada solicitud de homologación: razón social, domicilio, condición IVA, Ingresos Brutos, inicio de actividades, receptor y descripción del servicio. El CUIT/documento/ID IVA se contrastan con la solicitud fiscal reservada.
- Nueva migración **SQLite v13** (con respaldo previo) y snapshot JSON inmutable auditado con SHA-256; el operador lo registra solo con la solicitud `Preparada`, antes de enviarla.
- Se actualizan la pantalla fiscal, el PDF multipágina de prueba y la impresión térmica paginada. Los documentos siguen siendo **PRUEBAS SIN VALIDEZ FISCAL** y carecen de QR comercial.
- Se añade el codificador aislado de URL/JSON/Base64 de QR ARCA versión 1, inspirado en `Facturador.Reporting/ArcaQrService.cs` y en la especificación oficial. Está **desconectado de los cobros** y rechaza explícitamente homologación; no existe todavía autorización fiscal productiva ni QR impreso real.
- Nuevos tests de validación, integridad, snapshot y QR, añadidos al PR borrador. **No se han ejecutado todavía**: falta invocar manualmente la validación de Windows y revisar resultados.
- Alcance detallado, requisitos legales y restricciones en [docs/FACTURACION_V15.md](docs/FACTURACION_V15.md).

## Etapa v0.14: copias imprimibles de homologación (EN DESARROLLO)

- Se agregó una vista de solo lectura para solicitudes de **homologación autorizadas** con CAE de prueba de 14 dígitos y vencimiento. Bloquea borradores, rechazos e intentos todavía `PorConciliar`.
- La vista usa los datos fiscales reservados en SQLite: origen, CUIT emisor, clase, PV, número, documento e ID IVA del receptor, fechas del servicio, neto, alícuotas y total.
- Se incorporaron dos acciones restringidas a administradores en Configuración fiscal: **guardar PDF** en `Documentos/Aparcar/Homologacion` e **imprimir copia de prueba** en impresora Windows de 58/80 mm.
- Ambos formatos llevan varias leyendas de **PRUEBA SIN VALIDEZ FISCAL** y omiten el QR oficial. No se los debe entregar al cliente ni utilizar como factura electrónica.
- Se agregaron pruebas automáticas de la vista para autorización válida, CAE ausente, bloqueo de estados y conservación de importes.
- **Validación pendiente:** esta modificación todavía no tiene ejecución de CI ni prueba física de impresión. Ejecutar manualmente el workflow de validación cuando corresponda, sin generar artefactos.
- **Todavía faltan** datos comerciales obligatorios del emisor/receptor, QR fiscal correcto, plantilla legal de factura, controles de fecha, notas de crédito, reconciliación multi-terminal y homologación real autorizada. Nada de lo anterior habilita producción.

## Etapa v0.13: condición IVA oficial y conciliación fiscal estricta

**Validación:** [GitHub Actions #40241431501](https://github.com/matuky2005/aparcar/actions/runs/40241431501) — **66/66 pruebas aprobadas** y compilación WinUI correcta. **0 artefactos publicados**. Workflow devuelto a disparo exclusivamente manual.

### Condición IVA receptor: obtener opciones directamente desde ARCA
- Se añadió al cliente `ArcaHomologacion` la consulta SOAP **FEParamGetCondicionIvaReceptor** en homologación, autenticada con WSAA, firmando el TRA en memoria con el PFX del usuario.
- El catálogo filtra por **clase A, B o C**, respeta identificador, descripción y clase devueltos por ARCA, y rechaza respuestas sin opciones utilizables o con errores.
- En la pantalla de facturación se reemplazó el ingreso manual del ID por una lista oficial. El usuario debe **elegir** la condición IVA: Aparcar **no deduce** la situación tributaria del receptor por su documento.
- La selección se acepta solo si corresponde al mismo **CUIT emisor y tipo de factura** consultados durante los últimos 30 minutos. Si cambia CUIT o clase, debe consultarse de nuevo. El PFX no se copia a la base de datos y la contraseña del formulario se limpia al terminar la consulta.
- Un administrador con certificado de **homologación** habilitado puede obtener este catálogo. Las pruebas automatizadas usan HTTP/WSAA simulados, no consultan un contribuyente real.

### FECompConsultar: verificar alícuotas, además de los totales
- Antes era posible considerar coincidente una respuesta que tuviera igual importe total, neto e IVA, pero un **detalle de alícuotas distinto**.
- Ahora se compara cantidad de alícuotas y cada `Id`, `BaseImp` e `Importe` con el comprobante reservado, sin importar su orden. Un faltante, código distinto o diferencia de base/importe provoca `Coincide=false`, sin autorizar la conciliación ni un nuevo envío automático.
- Nuevas pruebas cubren catálogo oficial, rechazo de clase inválida, alícuotas divergentes y alícuotas ausentes.
- Para devolver `Autorizada`, sigue siendo obligatorio que el CAE y vencimiento sean válidos y que coincidan tipo, punto de venta, número y resto de los datos fiscales.

### Limitaciones
- **Solo homologación:** no existe emisión contra el servicio de producción ni se habilitan comprobantes con validez fiscal comercial.
- Falta validación real del esquema frente a un certificado ARCA autorizado, reconocimiento de las condiciones IVA del receptor y su vigencia, notas de crédito, factura PDF fiscal, impresora real, anticipo de cobros y conciliación multicaja.
- Para producción, también será necesaria una protección central de numeración entre terminales o aplicaciones y un tratamiento específico de rechazos/errores WSFEv1.
- No se generaron Portable, MSIX, ZIP ni otros instaladores y no se fusionó el PR con main.

Documentación oficial: https://wswhomo.afip.gov.ar/wsfev1/service.asmx?op=FEParamGetCondicionIvaReceptor y https://www.arca.gob.ar/ws/documentacion/ws-factura-electronica.asp .

## Etapa v0.12 (base fiscal): reutilización de patrones de Facturador

Se examinó el repositorio privado `matuky2005/Facturador` para adaptar al dominio de estacionamiento sus componentes:

- `src/Facturador.ARCA/Wsfe/WsfeCaeRequestBuilder.cs`: inspiró `src/Aparcar.Infrastructure/AdaptadorWsfeFacturador.cs`. Construye un `FeCAEReq` para **Factura A/B/C** con importes explícitos, IVA, documento, condición IVA, concepto de servicio y fechas.
- `src/Facturador.ARCA/Wsfe/WsfeResponseParser.cs`: inspiró validaciones estrictas de **identidad del comprobante, CAE numérico de 14 dígitos y vencimiento**, con resultados diferenciados `Autorizado/Rechazado/Indeterminado` y reconciliación de `FECompConsultar`. Una respuesta incompleta o distinta **no autoriza reenvío automático**.
- `src/Facturador.ARCA/Services/WsfeService.cs` y `WsfeSoapClient.cs`: inspiraron un nuevo método `ArcaHomologacion.ConsultarComprobanteAsync`, que usa WSAA y consulta **FECompConsultar en homologación**, sin solicitar CAE.
- `src/Facturador.Infrastructure/Data/ComprobanteRepository.cs`: se tomó como **criterio de seguridad para la próxima etapa** el guardado de un intento fiscal antes de enviarlo y el bloqueo de numeración mientras el estado esté pendiente o desconocido. **Esa reserva transaccional todavía NO está implementada en Aparcar**.

No se agregó dependencia de la aplicación WPF Facturador a la aplicación WinUI Aparcar: el adaptador es propio. Ambas mantienen bases, configuración y certificados separados. No se copió ningún certificado, secreto, token ni dato de clientes.

**Pruebas:** [GitHub Actions #40173927633](https://github.com/matuky2005/aparcar/actions/runs/40173927633) — **61/61 pruebas aprobadas**, dominio/infraestructura/WinUI compilan, **0 artefactos generados**. Workflow restaurado a disparo manual.

**Restricciones:** el constructor fiscal es una preparación validada **sin efecto fiscal**; `FECAESolicitar` todavía no se envía ni se expone al usuario. No puede considerarse listo para emitir comprobantes. Antes de hacerlo se necesita persistir intentos y reconciliar estados desconocidos con `FECompConsultar`, consultar tablas oficiales de condiciones de IVA y tipos, preparar los datos fiscales reales de cada pago, verificar categorías fiscales A/B/C y homologar con un certificado habilitado. Las notas de crédito y la producción están fuera de esta etapa.

Documentación oficial: https://www.arca.gob.ar/ws/documentacion/ws-factura-electronica.asp .

## Etapa v0.12: autorización fiscal de prueba, basada en Facturador

**Compilación y pruebas:** [GitHub Actions #40238678303](https://github.com/matuky2005/aparcar/actions/runs/40238678303): **64/64 pruebas aprobadas**, WinUI compilado, **0 artefactos publicados**. Workflow devuelto a modo manual.

### Código reutilizado de Facturador
Se tomó como referencia el código existente en el repositorio privado `matuky2005/Facturador`:
- `src/Facturador.ARCA/Wsfe/WsfeCaeRequestBuilder.cs` — construcción de `FeCAEReq`, validación de totales y alícuotas, fechas de servicio y condición IVA;
- `src/Facturador.ARCA/Wsfe/WsfeResponseParser.cs` — validación de CAE de 14 dígitos, vencimiento, tipo, punto de venta y número;
- `src/Facturador.ARCA/Services/WsfeService.cs` — patrón `FECompUltimoAutorizado`, `FECAESolicitar` y `FECompConsultar` antes de cualquier reintento;
- `src/Facturador.Domain/Rules/ValidadorCuit.cs` — control de dígito verificador de CUIT/CUIL.

Se incorporaron a Aparcar mediante un adaptador **independiente**, sin copiar bases de clientes ni certificados de Facturador.

### Nuevo circuito
1. Un cobro confirmado positivo de Estacionamiento, Lavadero o Abono genera un pendiente fiscal único y no fiscal de forma transaccional.
2. Un administrador completa CUIT emisor, PV, clase A/B/C, documento/condición IVA receptor, desglose neto/IVA/no gravado/exento, alícuota oficial y fechas de servicio. **No se inventan automáticamente** IVA, datos fiscales ni vencimientos.
3. `FECompUltimoAutorizado` en homologación consulta el último número y se reserva `último + 1` dentro de SQLite, comprobando que no haya otro borrador/conciliación abierta del mismo emisor/PV/tipo. Una solicitud es **única por origen y cobro**.
4. El usuario revisa el borrador y confirma explícitamente la **emisión de prueba**. **Antes de llamar a WSFEv1** Aparcar cambia su estado de `Preparada` a `PorConciliar` en una transacción auditada.
5. El cliente `ArcaHomologacion` firma WSAA en memoria y llama `FECAESolicitar` **exclusivamente al endpoint de homologación**. El CAE/vencimiento solo se registra si la respuesta aprobatoria corresponde al PV, tipo, número y formato esperados.
6. Ante corte de red, excepción, respuesta incompleta o duda, **no hay un segundo envío automático**. La solicitud conserva estado `PorConciliar` y exige `FECompConsultar`; solo se marca `Autorizada` cuando coinciden identidad y datos fiscales.
7. Una devolución posterior del cobro bloquea el envío de un borrador que todavía no se remitió.

Los estados de las facturas de prueba se conservan en `FacturasHomologacion` (migración v12) con datos serializados y SHA-256, auditoría y restricciones de unicidad. La clave PFX, certificado, CMS, token y sign **no se persisten** en estos registros.

**Limitaciones:** no existe emisión productiva, PDF fiscal, impresión de factura con QR oficial, notas de crédito, consulta dinámica de condiciones IVA receptor ni tratamiento integral de percepciones/tributos. El IVA y la clase de factura deben revisarse antes de preparar cada operación. Ante un rechazo explícito o un problema que no pueda conciliarse, se exige resolución manual; no se reciclan números ni se edita en caliente una solicitud ya reservada. Un hash de base local no impide la manipulación por un atacante que controle la computadora. Un número obtenido por `FECompUltimoAutorizado` no equivale a una reserva oficial exclusiva entre distintas cajas o aplicativos.

**No fue posible comprobar el servicio real con un PFX habilitado del contribuyente:** las pruebas de WSAA/WSFEv1 son SOAP simuladas y no producen CAE oficial. Se requiere homologación real, conciliación completa, certificación fiscal y ensayos de impresora antes de habilitar una caja comercial.

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

