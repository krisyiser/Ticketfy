# Correcciones de la primera revisión interna

La revisión con operadores de la edición 3.1.56 detectó problemas que la suite anterior no verificaba: botones fuera del área visible, campos redundantes y mensajes técnicos de validación. La edición 3.1.57 incorpora estas correcciones. No requiere cambios del esquema de la base.

## Cambios

- Contraseñas: mínimo de seis caracteres, compartido por configuración inicial, alta, edición y cambio de contraseña. Se conservan los hashes, el bloqueo de intentos, la verificación anterior y el filtro de claves comunes. No se obliga a acortar una contraseña existente. La decisión de longitud es de producto y no se presenta como cumplimiento de NIST.
- Productos: la sección de inventario contiene stock actual, mínimo, puntos de regalo, cantidad de reorden y pasillo/rack. El motivo aparece al modificar las existencias de un producto existente, para mantener su historial. Los campos SAT, IVA y máximo desaparecen de ese formulario; sus valores existentes se conservan al editar otros datos.
- Catálogos: los buscadores adicionales de proveedores y productos permanecen ocultos cuando la lista inicial está completa. Aparecen para catálogos mayores de una página, búsquedas en curso o reintentos de una consulta fallida, para que los registros posteriores sigan siendo accesibles.
- Gastos: botón de registro fijo, formulario desplazable, concepto obligatorio y validación de importes. La misma pantalla permite ingresos y retiros de efectivo mediante el servicio transaccional de caja. Esos movimientos tienen su propio historial del turno, paginado y autorizado; no se cuentan como ventas ni como gastos operativos.
- Compras: se eliminan propiedades intermedias que no notificaban el cambio de selección. La selección visible, el costo y el reinicio del formulario quedan sincronizados. El formulario completo se desplaza y mantiene el total y la confirmación visibles. Crédito registra cero pagado; al elegir otro medio se propone el total, permitiendo un pago parcial válido. Tarjeta y transferencia exigen referencia.
- Captura numérica: se respeta `decimal`, incluso en propiedades opcionales. Un texto pegado inválido no se transforma en otro importe. Los formularios corregidos impiden confirmar mientras un campo tiene error y muestran indicaciones en español sin nombres de excepciones.
- Presentación: etiquetas de casillas usan el color del tema, también en tema claro.

## Regresiones automatizadas

`InternalReviewRegressionTests` usa Semi, los estilos de producción, ventanas con áreas de 1280×630 y 1024×520, comprobación geométrica y pulsaciones de ratón. La simulación actualiza el renderizador antes de calcular coordenadas para evitar posiciones anteriores al diseño final.

Se verifican guardado y precisión de gastos, bloqueo de captura inválida, compras a crédito/efectivo/transferencia, stock, pagos, deuda y prevención de repetición. También se comprueban conservación de metadatos del producto, movimientos manuales con historial, paginación y rechazo de acceso a turnos ajenos. `InternalReviewCredentialTests` comprueba altas, autenticación y cambio con seis caracteres, y rechazo de cinco caracteres y contraseñas obvias.

## Revisión humana siguiente

1. En la edición de pruebas, abrir un turno con fondo ficticio y registrar un producto y proveedor.
2. Registrar una compra a crédito y otra con pago; verificar existencias, historial y saldo del proveedor.
3. Registrar un gasto, un ingreso y un retiro; comprobar sus historiales y el efectivo esperado.
4. Repetir en ventana reducida y con la escala habitual de Windows; verificar que siempre se alcanza el botón de guardar.
5. Pegar un importe inválido, corregirlo y guardar; comprobar que sólo existe un registro con el importe correcto.

La base operativa, Facturama, el servidor y el sitio de descargas permanecen fuera de esta intervención. La suite y estas correcciones no sustituyen un piloto ni certifican los demás módulos o el hardware real.
