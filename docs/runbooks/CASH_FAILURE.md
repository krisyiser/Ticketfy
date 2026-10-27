# Runbook — Diferencia o bloqueo de caja

1. Detener nuevos cobros y mantener el turno abierto.
2. Contar efectivo por denominación con dos responsables.
3. Comparar apertura, ventas por método, retiros, ingresos, devoluciones y cierre.
4. Revisar operaciones autorizadas o en conciliación; no repetirlas.
5. Registrar cualquier ajuste con motivo y autorización RBAC; nunca editar la DB.
6. Si la aplicación se cerró durante el cobro, reiniciar una vez y consultar el
   historial y los cobros pendientes de la caja original. La recuperación no
   descarta incertidumbre bancaria ni confirma efectivo sin evidencia.
7. Generar diagnóstico y respaldo verificado.

Escalar P1 por duplicado, cobro autorizado sin venta o diario incoherente; P2 por una
diferencia explicable pendiente de autorización.

**Detener espera** no cancela en banco y **Cerrar** no revierte una operación
enviada. Conservar la referencia local; un pendiente bloquea otro cobro y el cierre
del mismo turno. La resolución requiere comprobación supervisada, no otra venta.
Seguir el [procedimiento de cobros interrumpidos](../ENTERPRISE_RECOVERY_RUNBOOK.md#cobro-pendiente-o-respuesta-perdida).
En **Control comercial → Recuperar cobros**, usar **Consultar banco sin volver a
cobrar** con permisos de recuperación y conciliación. Consulta la orden original,
sin reenviarla; si falta su ID, obtenerlo del proveedor. Una etiqueta histórica
«completado», «fallido» o «cancelado» no sustituye la evidencia validada. Si la
consulta no confirma un resultado resoluble, conservar el pendiente y escalar sin
crear otra venta ni editar la base.

## Causas que muestra Control

En **Control → Conciliación**, consultar el día y revisar el texto de cada turno.
La pantalla distingue turno abierto, conteo ausente, atribución desconocida,
pagos/documentos incoherentes, movimientos de efectivo sin respaldo, saldo
almacenado diferente, cobros/transferencias pendientes y evidencia de cierre
incompleta. También muestra turnos con fecha inválida, sin atribuirlos al día
seleccionado.

Si el saldo almacenado difiere de la suma de movimientos, no registrar una entrada
o retiro ficticio ni intentar cerrar para que el programa lo corrija. Conservar
respaldo y diagnóstico, detener la caja afectada y escalar para determinar la
causa. La reparación de datos dañados requiere un procedimiento autorizado;
la consulta de conciliación no modifica registros.

Una diferencia entre efectivo contado y esperado sí puede conservarse en un
cierre válido: se registra como diferencia, no como un movimiento que la oculta.
Ese turno seguirá requiriendo revisión. «CONCILIADO INTERNO» no sustituye el
conteo físico, el estado del banco ni la revisión de existencias y cuentas.
