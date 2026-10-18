# Etapa 6 — Base comercial y conciliación

Fecha de corte: 18 de octubre de 2026. Trabajo local; candidato de desarrollo.

## Dictamen

Se implementó una base transaccional con documentos y movimientos trazables para
ventas, devoluciones, inventario, crédito, proveedores y caja. **La Etapa 6 no está
cerrada comercialmente:** no se ha ejecutado un piloto real, no hay hardware
conectado y no se ha migrado la base operativa en esta intervención.

La aprobación no debe deducirse del número de pruebas ni equipararse a eleventa,
Aspel Caja o SICAR. Las pruebas automatizadas validan escenarios concretos, no
paridad funcional completa ni certificación de equipos.

El [dictamen de madurez comercial](PRODUCT_READINESS.md) reúne el estado de todas
las etapas, prioridades y rangos orientativos para piloto y comercialización.

La validación posterior de plataforma está en [Migración a .NET 10](NET10_MIGRATION.md).
Los resultados de esta página conservan la evidencia de los cambios funcionales
de Etapa 6; la suite ampliada incluye las regresiones de la nueva plataforma.

## Cambios por bloque

| Bloque | Implementación | Límite importante |
|---|---|---|
| Modelo financiero | SaleLine con costo histórico; SalePayment por medio; validación exacta del total; ReturnLine y PaymentRefund; evidencia de autorización externa | La confirmación del controlador/banco no se simula en producción; reembolsos externos pendientes se concilian |
| Ledgers | InventoryMovement, AccountEntry de clientes/proveedores/monedero/lealtad, SupplierPayment, SupplierReturn y diario; bajas lógicas y cancelación de gastos | Los saldos iniciales migrados no reconstruyen un historial que nunca existió |
| Turnos y caja | RegisterId, ShiftId, CashierUserId; un turno por caja; cierre y arqueo atómicos; ingresos/retiros; comprobación documental de efectivo | Varias cajas se prueban con contextos locales; no es certificación de despliegue SQLite en carpeta de red |
| Flujos comerciales | Promociones revalidadas al cobrar, productos activos, mínimos/máximos/unidad, compras contado/crédito, estados de cuenta, abonos y recargas por efectivo o transferencia verificada | Abonos y recargas con tarjeta siguen restringidos; la transferencia requiere comprobación manual, no conexión automática al banco |
| Seguridad/reportes | Permisos en servicios y navegación; usuario real; diario administrativo; utilidad con costo histórico; pagos, devoluciones y efectivo separados | No sustituye una revisión de seguridad independiente; persisten módulos heredados por desacoplar |
| Pruebas | Idempotencia, permisos, múltiples cajas, devoluciones por medio, compras, recuperación, cierre interrumpido y UI headless | El piloto con dinero, terminal e impresora reales sigue pendiente |

## Garantías y decisiones

- Un comando monetario requiere clave de operación. Repetir la misma clave no
  crea otro asiento; cambiar su contenido produce rechazo.
- El backend exige turno abierto propio y usuario activo. Una venta no se acepta
  con pagos insuficientes, tarjeta sin prueba persistida o existencias negativas.
- Inventario, deuda, monedero, efectivo y diario se confirman junto al documento.
  Los callbacks e impresión posteriores no deshacen ni repiten el cobro.
- Las devoluciones conservan la venta y sus pagos originales. Restauran el costo
  histórico y, cuando corresponde, los componentes originales del kit.
- Los gastos se cancelan con motivo y confirmación de recuperación del dinero;
  no se eliminan físicamente.
- El cierre no inventa un movimiento para cuadrar diferencias. El conteo real y
  la diferencia permanecen visibles; los cobros inciertos impiden cerrar.
- Kardex y estados de cuenta muestran documento y atribución. La migración se
  identifica como sistema, sin atribuirla falsamente al cajero.

## Interfaz operativa

El módulo **Control** reúne Kardex, estados de cuenta, pagos/devoluciones de
proveedor, movimientos de caja, monedero, históricos y transferencias. Sus pestañas y comandos
requieren los permisos correspondientes.

**Históricos** permite confirmar el saldo actual de clientes, incluidos los
inactivos, y de compras antiguas. Exige vista previa vigente, referencia de la
evidencia, motivo y permiso `history.reconcile` (sólo administrador por defecto).
Los ajustes quedan en asientos nuevos y conservan el saldo anterior.

**Transferencias** guarda primero el destinatario e importe del abono o recarga.
La confirmación bancaria se conserva antes de aplicar el saldo: un fallo deja un
intento recuperable, no otro cobro. Una referencia no puede financiar dos ventas,
abonos o recargas. Los intentos pendientes bloquean el cierre del turno. Véase el
[procedimiento de históricos y transferencias](runbooks/HISTORICAL_BALANCES_AND_TRANSFERS.md).

Los cobros interrumpidos se resuelven allí después de verificar fondos y motivo.
La resolución exige la identidad/caja/turno original y permiso de conciliación;
un supervisor debe organizar esa revisión sin prestar credenciales ni atribuirse
la venta de otro usuario. No se vuelve a solicitar un cargo al proveedor.

Los turnos históricos sin propietario se cierran mediante revisión explícita,
conteo físico y referencia del acta. Esto permite abrir un turno nuevo, pero no
convierte el cierre antiguo en un historial conciliado.

El panel inferior **Periféricos** actualiza detección cada 30 segundos y conserva
el resultado del último envío a impresión. Permite una prueba de entrada del
lector sin crear ventas. No almacena el código escaneado en mensajes o logs.
Cola/puerto detectado no significa funcionamiento físico comprobado. Véase el
[runbook de impresión](runbooks/PRINT_FAILURE.md).

## Migración e históricos

La migración `20261018213827_CommercialLedgersAndRegisterOwnership` incorpora
entidades, índices, restricciones y triggers. El inicializador respalda antes de
actualizar. La migración no debe ejecutarse por primera vez sobre la única copia
de una base de negocio.

La migración `20261018224634_HistoricalBalancesAndIncomingTransfers` incorpora
actas de conciliación, intentos y recibos de transferencia inmutables. Registra
como consumidas las transferencias de ventas ya documentadas en `sale_payments`,
sin generar nuevos ingresos. Las referencias duplicadas impiden migrar; no se
eliminan ni fusionan automáticamente registros para superar esa validación.

Las compras antiguas se señalan como no conciliadas hasta confirmar su saldo
actual con evidencia. Después pueden recibir pagos sin volver a ingresar stock.
En clientes, el ajuste también repara diferencias entre el saldo almacenado y
sus asientos. Una revisión sin cambio monetario conserva igualmente un acta.

Conciliar un saldo **no reconstruye ventas, pagos, costos ni mercancía antigua**.
Las ventas y compras históricas sin partidas verificables siguen sin admitir
devoluciones por el nuevo flujo. Hace falta un procedimiento adicional de
incorporación de documentos completos; aprobar un saldo no lo sustituye.

El dinero se trata en decimal. Persisten cantidades heredadas double en DTOs,
validadas en backend; no se usan como importes financieros. La inmutabilidad tiene
protección EF y SQLite, no resistencia absoluta frente a un administrador del
archivo.

## Evidencia automatizada

Los archivos `Stage6CommercialLedgerTests`, `Stage6CommercialFailureTests`,
`Stage6ReconciliationTests`, `Stage6CommercialUiTests`,
`Stage6HardwareFeedbackTests` y `Stage6HistoricalAndTransferTests` añaden regresiones sobre SQLite temporal y UI
Avalonia headless. Los dobles de terminal/impresora están exclusivamente en pruebas.

La puerta de cobertura incluye ventas y devoluciones, clientes, compras,
proveedores, gastos, caja, monedero, promociones, kits, conciliación histórica, transferencias,
idempotencia, autorización, respaldo, inicialización, terminal y cola fiscal.
El umbral agregado se mantiene en 75%; los resultados exactos de la última
ejecución se registran al final de este documento.

Además se exige al menos 70% por cada servicio incluido, salvo el inicializador
de base (cuyas rutas de plataforma/arranque permanecen en el umbral agregado).
Se incluye CategoryService porque modificar categorías puede afectar promociones.

La suite no equivale a probar físicamente desconexiones eléctricas, papel agotado
o transacciones bancarias. Las pruebas de fallo inyectan errores y estados de
recuperación de forma controlada.

## Pendientes de salida comercial

1. Ensayar actualización/restauración sobre copias autorizadas de cada base
   comercial soportada y conciliar saldos históricos con el negocio.
2. Ejecutar el [piloto controlado](runbooks/COMMERCIAL_PILOT.md), inicialmente sin
   hardware y después con periféricos y cuenta de pago reales autorizados.
3. Completar abonos/recargas con tarjeta y devolución de documentos históricos
   incompletos, el mantenimiento restante de catálogos y las integraciones fiscales aplazadas.
4. Medir grandes catálogos e historiales y paginar consultas; continuar retirando
   acceso directo a datos y composición manual en módulos heredados.
5. Completar pruebas manuales de usabilidad, autorización administrativa y
   recuperación operativa; realizar revisión de seguridad independiente.
6. Generar release desde commit limpio, revisar vulnerabilidades al publicar y
   firmar ejecutable/instalador. Un paquete local sin firma no es distribución final.

No se ejecutaron cobros reales ni se publicaron cambios a servidores por esta
validación. La decisión sobre un entorno real permanece en manos del propietario.

## Verificación anterior a históricos y transferencias

Esta evidencia corresponde al candidato anterior de la misma etapa. Los paquetes
que se enumeran aquí no incluyen los cambios posteriores de conciliación histórica
y transferencias; no deben usarse para probar esos flujos.

| Comprobación | Resultado observado |
|---|---|
| Build Release Desktop/Tests y Keygen | Correcto; advertencias tratadas como errores |
| Suite con `coverage.runsettings` | 155 aprobadas, 0 fallidas, 0 omitidas |
| Cobertura crítica (23 archivos) | 86,62%: 2518 de 2907 líneas; mínimo agregado 75% |
| Mínimo individual | Todos los servicios incluidos superan 70%; inicialización se evalúa en agregado |
| Formato solución y Keygen | `--verify-no-changes --severity error` correcto |
| Modelo y migraciones | `has-pending-model-changes`: sin diferencias |
| NuGet Desktop, Tests y Keygen | Consulta de vulnerabilidades directas/transitivas sin paquetes reportados en esta ejecución |
| `git diff --check` | Sin errores de whitespace; avisos de normalización LF/CRLF |
| Dos paquetes Windows x64 | Mismo SHA-256; no instalados ni ejecutados |

Evidencia local ignorada por Git:

- `artifacts/stage6-quality-gate/stage6-quality.trx`.
- `artifacts/stage6-quality-gate/351cfa35-fd38-43c4-9ea6-a7fac18f32c8/coverage.cobertura.xml`.
- `artifacts/stage6-package-a/Ticketfy.Desktop-3.1.56-win-x64-ci.zip`.
- `artifacts/stage6-package-b/Ticketfy.Desktop-3.1.56-win-x64-ci.zip`.

SHA-256 de ambos paquetes:
`C3B2ABE86F2543F872486DA0E1E97FF804A779C8C1DF897FE70E0297F042F56B`.

Los manifiestos indican `source_state: dirty` y `signed: false`: se comprobó
repetibilidad del árbol de trabajo local, **no** una release desde commit limpio.
El commit base es `931839954895d2574b83c197b4135f4ff501d2d5`; los cambios de esta
etapa están en el árbol de trabajo, no contenidos en ese commit. No se declara
una revisión de seguridad independiente a partir de la auditoría NuGet.

## Verificación de históricos y transferencias

Continuación local del 18 de octubre de 2026 con datos sintéticos. Se añadieron
17 pruebas, incluidas recuperación tras fallo de escritura, referencias bancarias
reutilizadas, deuda histórica parcialmente pagada, ajuste cero, clientes inactivos,
matriz de permisos y comandos de interfaz. No se ejecutó el piloto manual ni se
abrió o migró la base operativa.

| Comprobación | Resultado observado |
|---|---|
| Suite completa Release con cobertura | 172 aprobadas, 0 fallidas, 0 omitidas |
| Build Release Desktop/Tests | 0 advertencias, 0 errores; advertencias tratadas como errores |
| Cobertura crítica (25 archivos) | 87,45%: 2760 de 3156 líneas |
| HistoricalReconciliationService | 96,47%: 82 de 85 líneas |
| IncomingTransferService | 96,80%: 121 de 125 líneas |
| Gate de cobertura | Supera 75% agregado y 70% por servicio; se mantiene la excepción del inicializador |
| Modelo/migraciones | `has-pending-model-changes`: sin diferencias |
| Formato solución | `--verify-no-changes --severity error`: correcto |

Evidencia local:

- `artifacts/stage6-followup-final/stage6-followup.trx`.
- `artifacts/stage6-followup-final/2fd5af66-f9d0-463a-8407-2824687eb0b8/coverage.cobertura.xml`.

Los cambios permanecen en el árbol de trabajo local, sin commit ni publicación.
Esta continuación no generó un instalador nuevo. La Etapa 6 sigue abierta por los
pendientes de salida comercial descritos arriba.

## Verificación de integridad de turnos y cierres

Continuación local del 18 de octubre de 2026. El cierre y la conciliación diaria
comparten `ShiftLedgerValidator`, incluido en el gate de cobertura. Se comprueba
atribución, estado e identidad de pagos; respaldo de retiros/ingresos y de
cancelaciones de gastos; saldo almacenado y evidencia de arqueo/cierre. El informe
explica las causas y no oculta turnos con fecha ilegible.

Se añadieron 20 casos de prueba. Once casos de inconsistencia inyectada fallaron
contra la versión anterior y pasan con la corrección. No representan incidentes
de producción ni una prueba de resistencia absoluta a manipulación del archivo.

| Comprobación | Resultado observado |
|---|---|
| Suite completa Release con cobertura | 192 aprobadas, 0 fallidas, 0 omitidas |
| Cobertura crítica (26 archivos) | 87,66%: 2834 de 3233 líneas |
| ShiftLedgerValidator | 94,29%: 66 de 70 líneas |
| Mínimos de cobertura | 75% agregado y 70% individual satisfechos; excepción del inicializador conservada |
| Build Release Desktop/Tests | 0 advertencias, 0 errores |
| Formato solución | `--verify-no-changes --severity error`: correcto |
| Modelo/migraciones | Sin cambios de esquema ni diferencias de modelo |
| `git diff --check` | Correcto; avisos de normalización LF/CRLF |

Evidencia final local:

- `artifacts/stage6-shift-integrity-verified/stage6-shift-integrity.trx`.
- `artifacts/stage6-shift-integrity-verified/d889df38-4c2c-4373-9434-ceddfb0a70cd/coverage.cobertura.xml`.
- Reproducción previa: `artifacts/stage6-reconciliation-red/regressions-before.trx`.

La consulta señala «CONCILIADO INTERNO» sólo para estas invariantes. No reemplaza
la conciliación bancaria, inventario físico o estados de cuenta completos. No se
abrió la base operativa, no se ejecutaron cobros reales y no se publicó ni generó
un nuevo instalador. Cambios locales sin commit.
