# Manual de contingencia y soporte

## Severidades

| Nivel | Ejemplo | Acción inicial |
|---|---|---|
| P1 | DB no abre, dinero duplicado, cobro sin venta, posible pérdida de datos | Detener ventas, conservar evidencia y escalar inmediatamente |
| P2 | Caja no cuadra, impresora principal caída sin alternativa, cola fiscal detenida | Aislar flujo afectado y atender el mismo turno |
| P3 | Falla cosmética, reporte no crítico, periférico alterno disponible | Registrar y programar corrección |

## Evidencia mínima

- hora local y zona horaria;
- versión de Ticketfy y Windows;
- usuario/rol, sin contraseña ni PIN;
- folio/referencia interna afectada, nunca PAN ni token;
- pasos observados y resultado esperado;
- ZIP de diagnóstico y SHA-256;
- fotos sólo si no muestran datos personales.

## Reglas de preservación

No borrar archivos, no ejecutar reparaciones SQLite manuales, no editar tablas, no
repetir cobros y no reinstalar durante un P1. Copiar el respaldo más reciente a medio
de sólo lectura y registrar quién lo custodió.

## Validación posterior a recuperación

Cuadrar ventas, pagos, caja, deuda e inventario; revisar el diario transaccional y la
cadena de auditoría; comprobar folios; ejecutar respaldo nuevo y prueba de
restauración. El incidente sólo se cierra con causa, acción, evidencia y responsable.

## Runbooks

- [Fallo de actualización](runbooks/UPDATE_FAILURE.md)
- [Diferencia o bloqueo de caja](runbooks/CASH_FAILURE.md)
- [Fallo de impresión](runbooks/PRINT_FAILURE.md)
- [Fallo SAT/fiscal](runbooks/SAT_FAILURE.md)
