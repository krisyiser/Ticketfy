# Etapa 3 — Integraciones comerciales reales

Fecha de corte: 18 de octubre de 2026.

## Estado

La base local y los simuladores están implementados. La etapa **no está cerrada**: faltan las pruebas end-to-end con una cuenta Facturama sandbox/productiva, una cuenta y terminal Mercado Pago reales y cada modelo físico que se pretenda certificar. El servidor de webhooks continúa fuera de servicio por decisión del proyecto; el receptor local queda preparado, pero no se publica ningún endpoint.

## Garantías implementadas

| Área | Garantía | Evidencia |
|---|---|---|
| Venta → fiscal | La venta y la orden fiscal se guardan en la misma transacción SQLite. No se timbra antes de confirmar cobro, inventario, caja y deuda | `SaleService.SaveAsync`, tabla `fiscal_operations` |
| Cola fiscal | Clave de idempotencia única, lease recuperable, backoff acotado, máximo de intentos y estados `Pending`, `WaitingRetry`, `NeedsReconciliation`, `Completed` y `FailedPermanent` | `FiscalIntegrationQueueService` |
| Respuesta incierta | Un corte de red después de emitir/cancelar no provoca repetición ciega. La operación queda en conciliación manual; si ya existe `ProviderDocumentId`, se consulta antes de continuar | `FacturamaProviderException.DeliveryUncertain` |
| CFDI | Emisión, consulta, descarga XML/PDF, cancelación con motivos `01`–`04`, UUID sustituto obligatorio para `01`, persistencia atómica y SHA-256 de archivos | `FacturamaService`, `FiscalIntegrationQueueService` |
| Terminal | Migración de Payment Intents heredada a Mercado Pago Orders API; referencia externa correcta, ID de order del proveedor y `X-Idempotency-Key` persistidos | `MercadoPagoTerminalService` |
| Polling | Consulta por `/v1/orders/{order_id}`, no por la referencia local; timeout o interrupción pasan a conciliación y nunca autorizan la venta | `MercadoPagoTerminalService` |
| Webhooks | Validación HMAC con reloj acotado, inbox deduplicado y almacenamiento exclusivo de metadatos/hash; la verdad se recupera consultando la API | `MercadoPagoWebhookVerifier`, `IntegrationWebhookInbox` |
| Hardware | Catálogo tipado de capacidades, parser de báscula que sólo acepta tramas estables y buffer para lectores HID | `Core/Hardware/HardwareCompatibility.cs`, `SerialScaleService` |

Facturama documenta `IdCfdi` para reutilizar un registro inválido/no contestado, la descarga por formato y tipo, y la cancelación con motivo/UUID sustituto. Referencias: [crear CFDI](https://apisandbox.facturama.mx/docs/Api/POST-3-cfdis), [descargar XML/PDF](https://apisandbox.facturama.mx/docs-multi/api/GET-Cfdi-format-type-id), [cancelar CFDI](https://apisandbox.facturama.mx/docs/Api/DELETE-api-cfdi-id_type_motive_uuidReplacement).

Mercado Pago indica que Orders API sustituye Payment Intents para Point, exige referencia externa e idempotencia, devuelve un `order_id` que debe conservarse y cancela mediante `POST /v1/orders/{order_id}/cancel`. Referencias: [migración a Orders API](https://www.mercadopago.com.mx/developers/es/docs/mp-point/resources/migrate-payment-intent-to-orders), [procesamiento Point](https://www.mercadopago.com.mx/developers/es/docs/mp-point/payment-processing), [Webhooks](https://www.mercadopago.com.mx/developers/es/docs/wix/additional-content/your-integrations/notifications/webhooks).

## Matriz de hardware

Ningún modelo comercial se declara “certificado” sin evidencia física. `Compatible por protocolo` significa que el controlador habla el estándar, no que ese SKU haya pasado laboratorio.

| Familia | Transporte/protocolo | Estado actual | Pruebas necesarias para certificar un SKU |
|---|---|---|---|
| Impresora térmica 58/80 mm | Windows RAW/Spooler + ESC/POS | Compatible por protocolo | acentos CP858, logo, QR, corte, atasco, sin papel, reinicio y 1,000 tickets |
| Impresora serial | COM, 8N1, ESC/POS | Compatible por protocolo | baud del fabricante, desconexión, timeout y recuperación sin ticket duplicado |
| Impresora de red | TCP RAW `tcp://host:9100`, ESC/POS | Compatible por protocolo | conexión, corte de red, timeout, recuperación y cola bajo carga |
| Cajón RJ11/RJ12 conectado a impresora | `ESC p`, pin 2 o 5 | Compatible por protocolo | pulso correcto, cajón desconectado, prohibición por RBAC y apertura única por venta |
| Lector de códigos | USB HID, sufijo CR/LF | Compatible por protocolo | EAN-8/13, UPC-A, Code128, caracteres inválidos, doble lectura y latencia |
| Báscula | Serial ASCII, kg/g, lectura marcada estable | Compatible por protocolo | protocolo exacto del fabricante, tara, negativo, inestable, desconexión y calibración metrológica |
| Mercado Pago Point | Orders API, terminal en modo PDV | Implementado; sin terminal real validada | aprobación, rechazo, cancelación `created`/`at_terminal`, timeout, webhook duplicado y conciliación |

La matriz certificada final debe registrar fabricante, modelo exacto, revisión/firmware, VID/PID o interfaz, configuración, sistema operativo, fecha, responsable, evidencia y resultado. Un cambio de firmware exige regresión mínima.

## Guion end-to-end pendiente

### Facturama

1. Configurar credenciales sandbox protegidas y código postal fiscal real del emisor.
2. Emitir un CFDI y verificar una sola fila de cola, un solo folio/UUID y coincidencia de importes/redondeo.
3. Cortar la red antes, durante y después del `POST`; comprobar que no se emite un segundo CFDI.
4. Reiniciar Ticketfy con una operación en lease y verificar su recuperación.
5. Descargar XML/PDF, validar SHA-256, abrir ambos y contrastar UUID/total/RFC.
6. Cancelar con motivos `01`–`04`; para `01`, comprobar sustitución; conciliar estados `requested`, `canceled` y `rejected`.

### Mercado Pago Point

1. Asociar una terminal de prueba/productiva en modo PDV. Las pruebas reales requieren cuenta productiva y medios de pago reales; el dispositivo virtual sirve para funcionalidad, no para medir calidad ([guía oficial de pruebas](https://www.mercadopago.com.mx/developers/es/docs/mp-point/integration-test)).
2. Probar aprobación, rechazo, expiración, cancelación antes y después de llegar a terminal y desconexión durante autorización.
3. Verificar que la repetición usa la misma referencia y clave de idempotencia y nunca crea dos órdenes.
4. Publicar HTTPS cuando vuelva el servidor, configurar el tópico `orders`, validar firma, responder `200/201` con rapidez y procesar después.
5. Repetir cada webhook y verificar deduplicación; consultar siempre `/v1/orders/{order_id}` antes de confirmar localmente.
6. Conciliar contra el reporte de actividades y comprobar monto, venta, `external_reference`, order y pago.

### Hardware físico

Ejecutar el plan de la matriz con al menos dos equipos Windows 10/11 y conservar evidencia. No añadir un SKU como certificado por similitud de marca o protocolo.

## Criterio de salida

La etapa se cierra únicamente cuando:

- todos los escenarios anteriores pasan con credenciales y hardware reales;
- la matriz contiene al menos un modelo certificado por categoría que se venderá como soportada;
- no queda ninguna operación fiscal o de pago sin resolución después de la prueba de corte/reinicio;
- los totales locales, Facturama, Mercado Pago, XML y reportes de conciliación coinciden;
- el endpoint HTTPS de webhooks supera firma válida/inválida, duplicados, reordenamiento y reintentos.

Hasta entonces, el producto conserva `FacturamaEnabled = false` y no afirma certificación de hardware.

## Evidencia local de verificación

- Suite Release Windows `win-x64`: **82/82** pruebas aprobadas.
- Pruebas específicas de Etapa 3: outbox atómico con la venta, deduplicación, lease, respuesta fiscal ambigua, cancelación, descarga/hash de XML/PDF, Orders API, referencia/ID de proveedor, repetición de cobro, checkout en conciliación, firma e inbox de webhooks, payload CFDI, lector HID, báscula y destinos de impresora.
- Compilación: 0 advertencias y 0 errores.
- Modelo EF Core: sin cambios pendientes después de `20261018125019_Stage3CommercialIntegrations`.
- Auditoría NuGet: sin paquetes vulnerables conocidos en Desktop y Keygen según las fuentes configuradas.
