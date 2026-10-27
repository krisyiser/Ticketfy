# Manual de operación

## Apertura diaria

1. Iniciar Windows con la cuenta operativa habitual; no cambiar de usuario para abrir
   una base protegida con DPAPI.
2. Abrir Ticketfy y comprobar que no aparezca un error de migración o restauración.
3. Revisar impresora, papel, lector, báscula y cajón antes de abrir turno.
4. Abrir turno con el fondo contado por dos personas cuando la política del comercio
   lo requiera.
5. Si se cambió hardware o Windows, ensayar el flujo en una instalación con datos
   de prueba, sin cobro real ni registros ficticios en la base operativa.

## Durante el turno

- No repetir un cobro sólo porque la pantalla tarde. Consultar primero el estado de la
  operación y la terminal.
- No editar archivos `.db`, `.db-wal` o `.db-shm` ni copiarlos con Ticketfy abierto.
- Ante diferencia de caja, conservar el turno abierto y seguir el runbook de caja.
- Una falla de impresión no invalida una venta confirmada; reimprimir desde Historial.
- Si una operación queda en conciliación, no crear una segunda con otra referencia.
- **Detener espera** no cancela en banco. Conservar la referencia y seguir el
  [procedimiento de cobros interrumpidos](ENTERPRISE_RECOVERY_RUNBOOK.md#cobro-pendiente-o-respuesta-perdida).
  Ese pendiente bloquea otro cobro y el cierre del mismo turno hasta su resolución.

## Cierre

1. Detener nuevas ventas y esperar operaciones pendientes.
2. Ejecutar corte, contar efectivo y registrar la diferencia con motivo.
3. Confirmar que el respaldo de corte fue creado. Si falla, generar uno manual antes de
   apagar y copiar el mensaje de error al incidente.
4. Cerrar Ticketfy y después apagar Windows.

## Respaldos

La ruta recomendada es una unidad local protegida y replicada fuera del equipo. La
política se evalúa al iniciar y cada hora; inicialmente crea un respaldo cada 24 horas,
conserva 30 copias o 30 días y prueba la restauración del archivo nuevo. Cambios de
ruta/política se aplican al reiniciar.

Semanalmente, un responsable debe comprobar fecha, tamaño y resultado del respaldo.
Mensualmente debe ejecutar un simulacro en una instalación de prueba, nunca sobre la
caja productiva.

## Restauración guiada

Usar sólo respaldos producidos por Ticketfy. Seleccionar el archivo en Configuración,
prepararlo y reiniciar. Después validar:

- versión y ausencia de errores de migración;
- último folio conocido y últimas tres ventas;
- turno esperado, efectivo y movimientos;
- existencias de cinco productos de control;
- usuarios activos y permisos;
- cola fiscal/pagos sin duplicados.

Si cualquier punto difiere, detener ventas y aplicar el runbook de contingencia.

## Diagnóstico para soporte

En **Configuración → Sistema y Seguridad**, pulsar **Exportar diagnóstico**. Compartir
únicamente el ZIP generado y su SHA-256. Nunca enviar la base, `license.jwt`, carpetas
`Secrets`, certificados, tokens ni capturas con datos de clientes.
