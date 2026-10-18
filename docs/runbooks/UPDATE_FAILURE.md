# Runbook — Fallo de actualización

1. Detener el inicio repetido y registrar versión anterior/nueva y mensaje exacto.
2. No borrar `%LocalAppData%\Ticketfy`, DPAPI ni archivos WAL/SHM.
3. Exportar o copiar el crash log sanitizado y conservar el respaldo previo.
4. Si el error es de firma/hash, aislar el paquete y no volver a ejecutarlo.
5. Si falla una migración, detener operación; no ejecutar SQL manual.
6. Volver al ejecutable anterior sólo si su esquema es compatible y soporte lo autoriza.
7. Si se requiere restaurar, usar la restauración guiada con el respaldo previo.
8. Validar folios, cinco ventas, caja, stock, auditoría y un respaldo nuevo.

Escalar como P1 si la base no abre, una migración quedó parcial o existen diferencias
de dinero/inventario. Adjuntar diagnóstico, hashes y bitácora; nunca la llave maestra.
