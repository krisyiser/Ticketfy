# Runbook — Piloto comercial controlado

## Estado y seguridad

Pendiente de ejecución real. El propietario indicó que no hay periféricos
conectados. Hasta contar con autorización del entorno se usan bases temporales y
datos de prueba; no se migra la base operativa, no se cobran tarjetas ni se envían
solicitudes fiscales reales.

## Preparación

1. Registrar commit, versión de Windows, usuario de prueba, ruta de datos aislada,
   copia de origen autorizada y referencia de respaldo verificado.
2. Probar restauración en aislamiento y comprobar folios, existencias y saldos.
   No copiar claves DPAPI ni datos personales al repositorio o diagnósticos.
3. Definir cajas, cajeros, supervisor, permisos y fondos físicos de prueba.
   No compartir credenciales ni asignar a un usuario genérico.
4. Documentar los históricos no conciliados antes de iniciar. No asumir que un
   saldo ausente en el nuevo ledger equivale a cero.

## Guion sin hardware

| Escenario | Comprobación requerida |
|---|---|
| Producto y ajustes | Validaciones, duplicado de código, mínimos/máximos, motivo, versión de stock y Kardex |
| Promoción | Vigencia, alcance, prioridad y coincidencia entre carrito y backend |
| Cliente y proveedor | Alta, cambios, inactivación y conservación de documentos |
| Conciliación histórica | Saldo actual comprobado, acta, ajuste sin efectivo/stock, vista previa obsoleta rechazada; incluir clientes inactivos |
| Venta en efectivo | Turno/actor; un folio; pagos = total; cambio correcto; stock y costo histórico |
| Venta a crédito y abono | Cargo sólo por crédito; abono único; efectivo y estado de cuenta coincidentes |
| Monedero y lealtad | Carga respaldada, uso sin saldo negativo, devolución sin duplicar saldo |
| Abono/recarga por transferencia | Intento persistido, depósito verificado una sola vez, historial con referencia; no aumenta efectivo |
| Depósito interrumpido | Recuperar tras fallo de aplicación; cancelar sólo sin fondos o documentar devolución bancaria; cierre bloqueado mientras esté pendiente |
| Compra contado/crédito | Inventario, costo, cuenta por pagar; sólo contado afecta efectivo |
| Pago/devolución a proveedor | Saldo y documento; devolver mercancía no inventa entrada de efectivo |
| Gasto y cancelación | Registro original intacto; recuperación del dinero confirmada y trazable |
| Devolución parcial/total | Medio original, costo histórico, stock y redondeo; no exceder original |
| Doble clic/reintento | Repetir clave conserva un solo documento y movimiento; contenido distinto se rechaza |
| Cierre y recuperación | Conteo, diferencia, bloqueo por cobro incierto y cierre atómico |
| Impresión sin equipo | Aviso visible, venta conservada y ningún segundo cobro |

Los escenarios de terminal se ejecutan mediante la suite y sus dobles, no mediante
referencias ficticias introducidas en producción.

Para históricos y transferencias seguir el
[runbook específico](HISTORICAL_BALANCES_AND_TRANSFERS.md). Las referencias de
prueba sólo se introducen en bases aisladas. Conciliar un saldo no habilita
devoluciones de mercancía cuyo detalle histórico no está acreditado.

## Incorporación de hardware real

Registrar fabricante, modelo, firmware, controlador, puerto y resultado; no basta
el nombre de una cola. En Periféricos consultar detección y verificar manualmente:

- Impresora: papel, acentos, QR, corte, desconexión, cola atascada y reimpresión
  del mismo folio. Envío al controlador no confirma salida física.
- Cajón: pin y apertura física autorizada. Un cajón RJ11 sin sensor no se puede
  identificar como conectado mediante software.
- Lector: enfocar la prueba de entrada, escanear y verificar terminación Enter.
  No usar textos digitados como evidencia de detección automática del lector.
- Báscula: puerto, protocolo estable, unidad, tara y calibración correspondiente.
- Terminal: autorización expresa del comercio, límites e importes de prueba;
  cargo, rechazo, timeout, consulta, devolución y conciliación bancaria.

## Conciliación diaria y salida

Para cada caja/turno, comparar apertura + entradas de efectivo − salidas con el
conteo. Separar ventas, abonos, recargas, gastos, proveedores y devoluciones.
Comparar cada pago electrónico y devolución contra estado del proveedor, no
contra una captura de pantalla sin referencia.

Revisar además stock inicial + Kardex = stock final; deuda de clientes,
proveedores y monederos = suma de sus asientos; ventas netas y utilidad = ventas
emitidas menos devoluciones del período y costos históricos correspondientes.

Registrar fecha, responsable, documento, importe/cantidad esperados y observados,
causa, corrección compensatoria y evidencia. No editar asientos para cuadrar.

Detener el piloto ante duplicidad, saldo sin documento, pérdida de trazabilidad,
cobro incierto sin resolución o restauración no verificable. La salida exige
todos los escenarios aprobados, cero diferencias sin explicación, pendientes
externos resueltos, revisión independiente y aprobación del propietario. Este
documento es un guion, no evidencia de que el piloto ya ocurrió.
