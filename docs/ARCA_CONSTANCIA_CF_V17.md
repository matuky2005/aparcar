# Aparcar v0.17 — Constancia de Inscripción y Consumidor Final predeterminado

**Estado:** implementación técnica en el PR borrador. Exclusivamente homologación. No facturación comercial, no habilitación de producción, no compilación verificada de v0.17 y no instaladores.

## 1. Obtener datos desde Constancia de Inscripción de ARCA

Servicio **ws_sr_constancia_inscripcion**, método **getPersona_v2**, según el manual **v4.1 (marzo 2026)**.

- Endpoint de *homologación*: `https://awshomo.arca.gob.ar/sr-padron/webservices/personaServiceA5`.
- Antes de consultar, WSAA firma una solicitud para el identificador de servicio `ws_sr_constancia_inscripcion`, **diferente** de `wsfe`. Certificado y CUIT representada deben tener autorización para el servicio en homologación.
- Con `idPersona` = CUIT consultada y `cuitRepresentada` = CUIT emisor habilitado se recupera la constancia. No se consulta DNI directamente.
- El parser verifica CUIT solicitada = CUIT devuelta, `estadoClave=ACTIVO`, errores del padrón y contenido de los datos generales. Si falla, **no completa datos**.
- La pantalla permite consultar la constancia de un **receptor identificado por CUIT** y completar su nombre/razón social y domicilio fiscal. También permite consultar el **emisor** para preparar su perfil.
- El listado de impuestos se muestra **como referencia**, pero el sistema no inventa ni asigna automáticamente un `CondicionIVAReceptorId`. Para preparar una factura debe seleccionarse la condición IVA oficial devuelta por `FEParamGetCondicionIvaReceptor` y corroborarse su correspondencia real.
- El domicilio fiscal del padrón puede **no coincidir con el domicilio comercial** del emisor ni ser el domicilio que deba informarse en todos los comprobantes. Revisar antes de guardar.
- PFX, contraseña, CMS, token y sign no se guardan en base ni en auditoría; los formularios limpian la contraseña tras la consulta.
- Las consultas automatizadas se prueban con certificados temporales y SOAP simulado. **No se consultó ARCA real con certificado del contribuyente.**

## 2. Consumidor Final por defecto

- Preferencia optativa **por CUIT emisor**, almacenada en `PreferenciasDispositivos`, con permiso administrador y evento de auditoría.
- Cuando la preferencia está habilitada, existe una operación elegida y se ha consultado un catálogo oficial reciente de condiciones IVA para la clase **B o C**, se intenta sugerir Consumidor Final.
- La condición IVA **no se supone como número fijo**: debe haber exactamente una opción que diga `Consumidor Final` y que corresponda a la clase oficial consultada. Se toma su ID de la respuesta ARCA.
- La selección predeterminada usa documento `99` / número `0` y etiqueta `CONSUMIDOR FINAL`. Borra los datos de un receptor anterior para evitar mezcla de personas.
- El usuario puede reemplazar Consumidor Final por CUIT, CUIL o DNI informado manualmente. También puede activar **«El cliente solicita identificación»** para impedir aplicar la opción anónima.
- Aplicación automática solo si el campo de documento está vacío o tiene 0; **nunca** sobreescribe silenciosamente un receptor que ya tiene un documento.
- **Límite normativo vigente a octubre de 2026:** operaciones iguales o superiores a **$10.000.000** requieren identificación. `ReglasConsumidorFinalV17` bloquea documento 99/0 para dicho importe en la selección y en la validación final de WSFE. No se debe usar este importe sin revalidar la legislación en cada actualización. También pueden regir obligaciones particulares de identificación, independientemente del importe.
- Esto **no elige automáticamente factura A/B/C**, ni determina el IVA/las alícuotas por medios de pago, patente o categoría de estacionamiento.

## 3. Seguridad y limitaciones

- La constancia se consulta bajo autenticación de la empresa/representada. No hay extracción pública sin autorización y no se almacenan datos privados innecesarios.
- El intérprete SOAP utiliza lector XML con DTD prohibidas y respuesta limitada a 2 MB.
- Solo homologación. La implementación de producción requiere permisos habilitados, reglas legales vigentes, revisión tributaria, pruebas con CAE real y contingencias.
- Se añaden pruebas unitarias de CUIT incorrecta o inactiva, WSAA con servicio padrón, selección CF clase B/C, límites, preferencia y selección por solicitud del cliente.
- **Pendiente:** compilación WinUI en Windows y ejecución completa de tests manual desde el workflow GitHub Actions. Se mantiene `main` intacta y **0 artefactos publicados**.

## Documentación oficial

- [Manual Consulta a Padrón Constancia de Inscripción v4.1](https://www.arca.gob.ar/ws/WSCI/manual_ws_sr_ws_constancia_inscripcion.pdf)
- [Catálogo oficial de Web Services](https://www.arca.gob.ar/ws/documentacion/catalogo.asp)
- [ARCA — datos de comprobantes electrónicos](https://www.arca.gob.ar/fe/emision-autorizacion/datos-comprobantes.asp)
- [RG 5866/2026 — modificación de requisitos de identificación](https://biblioteca.arca.gob.ar/search/query/norma.aspx?p=t%3ARAG%7Cn%3A5866%7Co%3A9%7Ca%3A2026%7Cf%3A26%2F06%2F2026)
