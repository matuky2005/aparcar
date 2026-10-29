# Aparcar — etapa fiscal v0.15 (desarrollo)

**Estado:** preparación exclusivamente técnica. No habilitada para emitir facturas productivas ni para cobrar comercialmente.

## Implementación

- `FiscalDocumentoV15.cs`: captura separada de emisor, receptor, descripción del servicio y condición de venta; compara CUIT y documento/IVA receptor con la solicitud WSFEv1 reservada antes de aceptar los datos.
- `AparcarDb.FiscalV15.cs`: tabla `DatosDocumentosFiscalesPrueba`, única por solicitud; snapshot JSON protegido con SHA-256, registro auditable, solo con estado `Preparada`. No se permite sobreescribir.
- Esquema **13**: migración con respaldo previo, inicialización después del esquema fiscal v12, compatibilidad del verificador de respaldos hasta versión 13.
- Pantalla Configuración fiscal: campos comerciales y botón «Guardar identidad comercial inmutable». El CUIT y los datos WSFE del receptor proceden del borrador, no de texto libre.
- Vista de homologación: muestra el snapshot si existe, o avisa «DATOS COMERCIALES NO CAPTURADOS»; sin QR fiscal. PDF de prueba multipágina y ticket térmico paginado.
- `EspecificacionQrArca.cs`: implementación **aislada y no conectada al flujo** del formato JSON/Base64 de QR v1 publicado por ARCA. Rechaza explícitamente `AmbienteQrArca.Homologacion`. No representa verificación real del CAE y no puede activar la emisión productiva.
- Pruebas nuevas: cambios prohibidos, discrepancias CUIT/documento/IVA, integridad de snapshot, representación de prueba y estructura QR (incluyendo consumidor final sin identificar).

## Puntos a tener en cuenta antes de producción

1. **Identidad del emisor:** confirmar razón social, IIBB, domicilio y fechas oficiales vigentes, condición IVA real, datos por sucursal y PV habilitados. El programa no obtiene ni verifica actualmente esos datos con los padrones ARCA.
2. **Receptor:** confirmar datos fiscales/condición IVA, identificación exigible, facturación a consumidor final y umbrales legales en la fecha de emisión. Aparcar solo verifica que los campos coincidan con el borrador interno.
3. **Datos fiscalmente vinculados:** almacenar un snapshot completo como parte del comprobante emitido, incluyendo conceptos, alícuotas, cantidades, precios unitarios y medios de pago; preservar identidad al regenerar PDF sin usar valores de configuración cambiantes.
4. **QR comercial:** permitir el uso de `EspecificacionQrArca` únicamente tras construir un modelo específico de **CAE productivo verificado** contra la base y ARCA; no usar un simple flag manual como prueba de producción. El QR de homologación permanece prohibido.
5. **CAE:** antes de habilitar producción, comprobar sistema WSAA/WSFE real, tasas, restricciones de numeración multicaja, fecha de autorización, conciliación post-corte y notas de crédito.
6. **Documentación legal:** diseñar original/duplicado, encabezado, leyendas impositivas vigentes, transparencia fiscal cuando corresponda, representación completa del servicio, y pie fiscal. Revisar con especialista tributario.
7. **Salida real:** probar impresoras térmicas 58/80, espaciados, corte, codificación de caracteres, reimpresión, QR real, PDF A4 y recuperación desde SQLite.
8. **Seguridad:** control de acceso y respaldos cifrados fuera de la PC; SHA256 sin clave detecta modificaciones accidentales pero no protege contra un atacante con acceso al archivo SQLite.
9. **Usabilidad:** migrar de captura comercial por solicitud a perfiles fiscales versionados por CUIT/sucursal, evitando recargar campos repetitivos sin perder el snapshot histórico.

## Referencias oficiales

- ARCA, *Especificaciones del QR incluido en las facturas electrónicas*: https://www.arca.gob.ar/fe/qr/documentos/QRespecificaciones.pdf
- Biblioteca normativa ARCA, RG 1415, Anexo II (texto actualizado): https://biblioteca.arca.gob.ar/search/query/norma.aspx?p=t%3ARAG%7Cn%3A1415%7Co%3A3%7Ca%3A2003%7Cf%3A07%2F01%2F2003
- Documentación WSFEv1: https://www.arca.gob.ar/ws/documentacion/ws-factura-electronica.asp

## Validación de v0.15

El código se añadió al PR borrador y se hizo revisión estática; **no se ejecutó aún compilación ni batería de tests en Windows**. El workflow permanece manual y sin generación de artefactos. No fusionar a `main` sin una ejecución satisfactoria y revisión de seguridad fiscal.
