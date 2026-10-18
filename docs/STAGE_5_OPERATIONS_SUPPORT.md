# Etapa 5 — Operación y soporte

Fecha de corte: 18 de octubre de 2026.

## Estado

La operación local quedó instrumentada para que un administrador pueda respaldar,
probar, restaurar y obtener evidencia de diagnóstico sin manipular la base activa.
Facturama real, publicación de actualizaciones y servicios remotos continúan fuera de
alcance mientras la infraestructura esté en mantenimiento.

## Controles implementados

| Área | Garantía | Evidencia |
|---|---|---|
| Restauración | El archivo se abre en modo lectura, pasa `integrity_check`, contiene el esquema comercial mínimo, se restaura temporalmente y se compara por SHA-256 antes de quedar pendiente | `BackupService.PrepareRestoreAsync` |
| Aplicación segura | La base nunca se sustituye mientras está abierta. El cambio ocurre antes de migraciones en el siguiente arranque y conserva rollback si falla la validación final | `AppBootstrapper`, `BackupService.ApplyPendingRestoreAsync` |
| Respaldo automático | Se evalúa al iniciar y cada hora; cuando vence el intervalo usa SQLite Backup API, prueba una restauración temporal y aplica retención por antigüedad y cantidad | `BackupService.RunMaintenanceAsync`, `BackupMaintenanceWorker` |
| Diagnóstico | ZIP con entorno, resumen de respaldos, matriz de hardware y hasta tres logs sanitizados; excluye DB, licencia, DPAPI, credenciales y llaves | `DiagnosticExportService` |
| Telemetría | Opt-in explícito, desactivado por defecto, almacenamiento exclusivamente local y eventos limitados a nombre/fecha/éxito. Revocar consentimiento borra el buffer | `LocalOperationalTelemetry` |
| Operación | Manuales y runbooks versionados junto al código | `docs/` y `docs/runbooks/` |

## Flujo de restauración

1. En **Configuración → Sistema y Seguridad**, seleccionar un `.db` mediante
   **Examinar**.
2. Pulsar **Validar y preparar restauración**.
3. Ticketfy prueba una restauración temporal y crea `BeforeRestore` de la base activa.
4. El operador registra el mensaje y cierra Ticketfy normalmente.
5. En el siguiente inicio, Ticketfy comprueba manifiesto y hash, sustituye la base antes
   de abrir EF Core y después ejecuta las migraciones versionadas.
6. El operador confirma último folio, turno, existencias y saldo de caja conforme al
   [manual de contingencia](CONTINGENCY_SUPPORT_MANUAL.md).

Una restauración incompleta o alterada detiene el arranque. Nunca se intenta continuar
con una base cuya integridad o esquema no esté demostrado.

## Limitación criptográfica importante

Los respaldos SQLite conservan el cifrado de la instalación. La restauración guiada
está soportada en el mismo equipo y perfil de Windows que protege la llave DPAPI. Para
recuperación en otro equipo debe existir custodia externa previa de
`TICKETFY_DB_MASTER_KEY`; copiar `database-key.dpapi` no permite descifrarlo en otra
identidad. La llave nunca debe adjuntarse al diagnóstico ni guardarse junto al respaldo.

## Verificación automatizada

`Stage5OperationsTests` cubre:

- respaldo, integridad, hash y restauración temporal;
- preparación y aplicación en frontera de reinicio;
- rechazo de una DB íntegra pero ajena al esquema comercial;
- retención y omisión de una ejecución todavía no vencida;
- ejecución periódica y registro mínimo del resultado;
- ausencia de DB/secretos en el ZIP de diagnóstico;
- telemetría opt-in y borrado al revocar consentimiento;
- sanitización de Bearer tokens, query strings y perfiles de usuario.

## Criterio de salida

La etapa queda lista para cierre cuando la puerta integral esté verde desde un commit
limpio y se realice un simulacro operativo documentado en un equipo representativo.
La certificación de hardware y las pruebas SAT/terminal reales siguen dependiendo de
la Etapa 3.
