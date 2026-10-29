# Revisión de ventas, clientes y promociones

Continuación de la revisión interna del 28 de octubre de 2026. Los cambios se validan con bases SQLite sintéticas y servicios de terminal exclusivos de pruebas. No se interviene la base operativa ni se habilita Facturama. No hay cambios del esquema.

## Correcciones

- **Cobro:** cambiar de tarjeta a efectivo exige capturar el dinero recibido. Antes podía conservarse como efectivo el total propuesto para tarjeta. Los importes admiten punto o coma decimal, sin separadores de miles, exponentes ni más de dos decimales. El pago mixto valida cada componente; un campo visible con error no puede confirmar su valor anterior. El descuento manual recalcula total, cambio y conciliación de medios. El botón de confirmar tiene una fila propia para seguir accesible en ventanas cortas.
- **Clientes:** nombre, teléfono opcional, correo, código y límite de crédito comparten validación en formulario y servicio. Se rechazan códigos repetidos y reintentos de alta con el mismo identificador y otro perfil. La edición idéntica no genera una segunda entrada administrativa. Un alta incierta conserva el identificador y el perfil original al reintentar; una edición incierta exige volver a consultar antes de editar. Al crear un cliente desde el POS queda seleccionado; altas, ediciones y abonos refrescan también ese catálogo.
- **Abonos:** se captura un importe positivo de hasta dos decimales dentro del saldo mostrado, manteniendo las comprobaciones autoritativas del servicio. Si se pierde la respuesta, el reintento conserva el mismo documento e importe. El formulario bloquea cambios durante esa confirmación; un fallo posterior al guardado no vuelve a habilitar el pago. Cerrar o sustituir un diálogo impide que su respuesta tardía cierre otro. Este formulario registra efectivo; las transferencias conservan su flujo de confirmación bancaria.
- **Directorios e históricos:** consultar o cambiar la búsqueda de clientes retira filas anteriores y bloquea sus acciones. Se incorpora Actualizar y se acomodan las acciones en el espacio disponible. Los botones del historial de ventas tienen una fila adaptable. Los estados de cuenta presentan conceptos en español conservando documento, movimientos y saldo del ledger.
- **Promociones:** etiquetas específicas para porcentaje, importe y precio final; cantidades gratis visibles sólo cuando corresponden; campos de alcance ocultos al elegir todos los productos. Se rechazan cantidades vacías e importes mal formados. El buscador adicional aparece cuando se necesita acceder a más resultados o reintentar. El cambio de estado muestra un mensaje para el operador ante fallos técnicos. Desactivar una regla conserva el descuento histórico de tickets ya vendidos.

## Validación numérica

En Avalonia 11.2.5, corregir una captura inválida al mismo valor numérico anterior podía conservar el error porque el binding evita escribir un valor idéntico. `NumericTextBox` vuelve a leer la validación y revalida el texto exacto; no elimina indiscriminadamente errores ni sustituye una entrada rechazada por el valor anterior. Se verifica también una propiedad que rechaza valores negativos. Referencia técnica: [BindingExpression de la versión utilizada](https://github.com/AvaloniaUI/Avalonia/blob/11.2.5/src/Avalonia.Base/Data/Core/BindingExpression.cs).

## Evidencia automatizada

`CommercialFlowReviewTests` comprueba pulsaciones de ratón, campos inválidos, corrección al mismo valor, ventanas cortas, formatos es-MX/es-ES, pérdida de respuesta, callbacks fallidos y cierre de diálogos. Confronta ventas, medios, existencias, caja, estados de cuenta, descuentos y auditoría con la base de prueba. La aplicación headless carga Semi y el tema DataGrid en el ámbito de aplicación, como el programa real, para que las pruebas alcancen los controles de las filas.

Los resultados completos se generan mediante `eng/invoke-quality-gate.ps1`: compilación con advertencias como errores, suite, cobertura crítica, modelo/migraciones, dependencias, arranque aislado y paquete reproducible. Los informes TRX y cobertura quedan en `artifacts/quality-*/TestResults`; la CI conserva esos informes y el candidato correspondiente al commit.

## Revisión humana de la siguiente edición de pruebas

1. Crear un cliente con teléfono vacío y otro con teléfono válido; comprobar duplicados, edición, selección desde POS y actualización del directorio.
2. Abrir un turno ficticio, vender en efectivo y a crédito; revisar ticket, stock, caja, deuda y estado de cuenta.
3. Cambiar tarjeta a efectivo antes de cobrar: debe solicitar el importe recibido. Pegar un importe inválido, corregirlo al valor anterior y confirmar una sola vez.
4. Registrar un abono y comprobar un solo documento, reducción de deuda y aumento del efectivo esperado. Las pruebas automatizadas simulan la pérdida de respuesta.
5. Crear una promoción, vender con ella, desactivarla y verificar que la venta anterior conserva su descuento.
6. Repetir con la ventana reducida y el escalado habitual de Windows; revisar acceso a guardar, abonar, estado de cuenta, detalle, devolución y reimpresión.

No se certifica impresión o terminal real, escalado de todos los equipos ni preparación comercial general con estas pruebas. La prueba bancaria automatizada utiliza evidencia sintética y no ejecuta cobros reales.
