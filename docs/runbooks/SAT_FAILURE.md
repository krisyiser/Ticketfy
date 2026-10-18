# Runbook — Fallo SAT/fiscal

El proveedor real permanece deshabilitado mientras Facturama y el servidor estén fuera
de alcance. Este runbook aplica al reactivar la integración.

1. Confirmar que la venta y el cobro local están completos; facturación no debe alterar
   dinero, caja ni inventario.
2. Identificar la operación por su clave idempotente y estado de cola.
3. En timeout o respuesta incierta, pasar a conciliación: no reenviar a ciegas.
4. Consultar al proveedor por `ProviderDocumentId`/referencia antes de emitir otra vez.
5. Verificar UUID, RFC, total, XML y PDF; comparar sus SHA-256 persistidos.
6. Para cancelación, registrar motivo `01`–`04` y UUID sustituto cuando corresponda.
7. Conservar acuse y estado final. Una operación sin resolución permanece abierta.

Escalar P1 si hay dos UUID/folios para una venta o totales distintos; P2 si la cola está
detenida pero las ventas locales continúan coherentes.
