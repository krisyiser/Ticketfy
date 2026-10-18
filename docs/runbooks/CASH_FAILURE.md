# Runbook — Diferencia o bloqueo de caja

1. Detener nuevos cobros y mantener el turno abierto.
2. Contar efectivo por denominación con dos responsables.
3. Comparar apertura, ventas por método, retiros, ingresos, devoluciones y cierre.
4. Revisar operaciones autorizadas o en conciliación; no repetirlas.
5. Registrar cualquier ajuste con motivo y autorización RBAC; nunca editar la DB.
6. Si la aplicación se cerró durante el cobro, reiniciar una vez y dejar actuar la
   recuperación idempotente antes de consultar la terminal.
7. Generar diagnóstico y respaldo verificado.

Escalar P1 por duplicado, cobro autorizado sin venta o diario incoherente; P2 por una
diferencia explicable pendiente de autorización.
