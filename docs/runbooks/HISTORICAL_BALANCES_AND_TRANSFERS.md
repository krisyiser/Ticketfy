# Runbook — Saldos históricos y transferencias

## Entorno de prueba

Estos flujos se validan con SQLite temporal y datos sintéticos. No requieren
impresora, terminal ni conexión al banco. No abrir una base operativa para ensayar:
el arranque normal aplica migraciones. Una futura prueba con datos del comercio
requiere una copia autorizada, respaldo verificado y una ruta aislada.

La suite `Stage6HistoricalAndTransferTests` cubre importación desde el esquema
comercial anterior, recuperación, repetición de comandos, permisos y comandos de
interfaz Avalonia headless. No constituye un piloto manual ni verifica fondos
bancarios reales.

## Conciliar un saldo histórico

1. Entrar con permiso `history.reconcile`, concedido sólo al administrador por
   defecto. Puede asignarse expresamente desde la matriz de roles. No requiere
   turno abierto porque no se recibe ni entrega dinero.
2. En **Control → Históricos**, elegir cliente o compra antigua. El listado de
   clientes incluye inactivos; las compras modernas no aparecen como históricas.
3. Consultar la vista previa y el historial. Comprobar destinatario, documento,
   saldo de los asientos y saldo almacenado, si corresponde.
4. Indicar el **saldo actual comprobado**, motivo y referencia del acta o estado
   de cuenta conservado por el comercio. No introducir el total original de una
   factura que ya recibió abonos. La referencia identifica evidencia externa;
   esta pantalla no adjunta ni valida archivos.
5. Marcar la revisión de evidencia y registrar el ajuste. Si otro movimiento
   cambió el saldo, consultar una nueva vista previa antes de continuar.
6. Revisar el estado de cuenta y el acta. El ajuste no altera efectivo, inventario,
   ventas ni documentos originales. Una corrección posterior requiere otra acta.

En clientes, un saldo negativo representa saldo a favor. En compras históricas,
el saldo debe estar entre cero y el total original; un crédito adicional requiere
su propio documento. Tras conciliar una compra, pueden registrarse pagos contra
su deuda sin recibir nuevamente la mercancía.

Ejemplo sintético: una compra de $100 tiene deuda comprobada de $70. Se incorpora
un ajuste de $70; pagar $20 deja $50. Una revisión posterior debe indicar el saldo
actual que se comprobó, no repetir $70. El pago y su salida de caja permanecen.

No usar este procedimiento para ocultar faltantes de caja o crear ventas antiguas.
Las devoluciones de ventas/compras sin detalle verificable siguen bloqueadas:
conciliar deuda no acredita cantidades, costos ni medios originales de pago.

## Aplicar un abono o recarga por transferencia

Se requiere turno abierto propio, `payments.reconcile` y además `cash.in_out`
para abonar clientes o `wallet.manage` para recargar monederos. La confirmación es
manual y auditada: el programa no consulta el banco ni solicita un cargo.

1. En **Control → Transferencias**, elegir cliente activo o monedero activo.
   Indicar importe y concepto, y preparar el intento. No se modifica aún el saldo.
2. Verificar en la cuenta del comercio el depósito, importe y destinatario.
   No basta una imagen proporcionada por el cliente. En estas pruebas, emplear
   exclusivamente referencias sintéticas en la base aislada.
3. Seleccionar el intento guardado, escribir la referencia única del depósito,
   marcar la comprobación y pulsar **Confirmar y aplicar / recuperar**.
4. Comprobar el abono o recarga y el historial de depósitos. No debe aparecer una
   entrada de efectivo. La referencia queda consumida por ese documento y no
   puede reutilizarse en una venta, otro abono o una recarga.

Ejemplo de la prueba de interfaz: deuda inicial $100, saldo conciliado $80,
transferencia de $20, saldo final $60. Repetir el comando no crea un segundo abono.

## Resolver intentos pendientes

| Situación | Resolución |
|---|---|
| Reinicio o fallo después de confirmar el depósito | Volver con el mismo usuario, caja y turno, actualizar y seleccionar el intento; aplicar usando la referencia persistida. No solicitar otro pago |
| No hubo depósito | Comprobar ausencia de fondos, indicar motivo y descartar el intento. Se conserva el registro; no puede reutilizarse |
| Depósito confirmado que ya no puede aplicarse | Devolverlo realmente por el canal bancario y documentar referencia, motivo y comprobación en la pantalla. El botón no ejecuta el reembolso |
| Cierre bloqueado | Resolver todos los intentos del turno. No borrar filas ni sustituir usuario/caja para eludir el control |

Una confirmación persistida impide descartar el intento como si no hubiera dinero.
La referencia de devolución no puede ser la del depósito ni justificar otro
reembolso. Los depósitos ya aplicados no se revierten mediante el flujo de
pendientes. Los abonos y recargas con tarjeta continúan restringidos.

## Actualización y conciliación diaria

La migración `20261018224634_HistoricalBalancesAndIncomingTransfers` conserva como
consumidas las referencias de transferencias de ventas ya registradas. No crea
ingresos nuevos ni presupone pagos ausentes. Si hay referencias duplicadas,
detener la actualización y revisar la copia y el respaldo; no eliminar evidencia
para forzar la migración. El descenso de versión requiere restaurar el respaldo
previo y conciliar movimientos posteriores, no ejecutar un borrado de tablas.

Al cierre, comprobar por separado efectivo, transferencias aplicadas y depósitos
devueltos. Comparar las referencias con el estado bancario; los abonos y recargas
no son ventas adicionales. Conservar actas, referencias, fecha y responsable.
Las tablas de historial muestran hasta 200 registros recientes; no son un
extracto bancario ni un archivo contable completo.

La validación con datos del negocio, recuperación sobre copias representativas,
cuentas bancarias reales y revisión independiente sigue pendiente.
