# Etapa 1 — Estabilización del núcleo transaccional

Línea de base implementada localmente el 18 de octubre de 2026. Recuperación
actualizada el 20 de octubre: consultar el [estado del candidato empresarial](ENTERPRISE_REMEDIATION_PROGRESS.md)
para los hallazgos posteriores y las puertas de aprobación aún abiertas.

El alcance fiscal remoto (Facturama), servidor, sitio de descargas y publicación en Forgejo continúa suspendido por decisión del proyecto. La idempotencia fiscal externa se retomará cuando ese módulo vuelva al alcance; ningún servicio remoto es necesario para operar o probar el núcleo local descrito aquí.

## Invariantes del núcleo

- Cada venta, devolución y abono recibe una `OperationId` única. Repetir la misma operación devuelve el resultado ya confirmado y no vuelve a mover dinero, inventario ni folios.
- Venta, inventario, deuda, monedero, caja y `transaction_journal` se confirman dentro de la misma transacción SQLite.
- Los folios de venta y devolución se reservan con una secuencia atómica (`INSERT ... ON CONFLICT ... RETURNING`) dentro de la transacción.
- El stock se modifica con comparación de `stock_version` y condición de existencia suficiente. Una escritura competidora o insuficiencia cancela toda la operación.
- Deuda de cliente y saldo de monedero se modifican con actualizaciones atómicas condicionadas; no se permite cobrar más que la deuda ni gastar más que el saldo.
- Todo importe financiero se representa como `decimal` y SQLite lo persiste como centavos enteros mediante `SqliteDecimalConverter`. `double` queda reservado para cantidades fraccionarias, puntos, tasas o geometría de interfaz.
- Caja conserva la entrada original y registra devoluciones/cancelaciones como movimientos separados. El corte incluye ambas partes para no omitir ni duplicar efectivo.

## Migraciones y compatibilidad

El arranque usa únicamente `Database.MigrateAsync()`. `EnsureCreated` queda limitado a fixtures de prueba y no existe ningún parche de esquema de negocio durante el arranque.

Versiones comerciales soportadas y punto de partida validado:

| Versión | Migración base |
|---|---|
| 3.0.43 | `20260801054956_InitialCreate` |
| 3.0.44 | `20260808183950_AddSecurityFields` |
| 3.1.25 | `20260817060006_AddSatProductCodes` |

Las bases antiguas creadas sin historial de EF se adoptan solamente si contienen todas las tablas y columnas de la línea comercial soportada. La adopción escribe exclusivamente `__EFMigrationsHistory`; si la validación falla, el arranque se detiene sin modificar el esquema de negocio.

La migración `20261018111240_StabilizeTransactionalCore`:

- convierte importes `REAL` heredados a centavos `INTEGER` sin alterar su valor;
- agrega claves de operación y sus índices únicos;
- agrega `stock_version`;
- crea `checkout_operations` y `transaction_journal`;
- incorpora estados persistidos para ventas, abonos y devoluciones.

La base de auditoría separada también se crea mediante su propia migración versionada.

## Recuperación de cobros interrumpidos

Antes de llamar a una terminal se guarda una intención en `checkout_operations` con la venta completa y su clave idempotente.

1. `Started`: existe intención local. Al reiniciar se revisa la evidencia bancaria; un pago confirmado puede continuar hacia recuperación. Efectivo sin comprobar o un resultado bancario incierto requieren conciliación, no un descarte automático.
2. `PaymentAuthorized`: existe autorización para recuperar con la misma `OperationId`. El efectivo, incluido el de un pago mixto, requiere comprobación supervisada; todavía se validan identidad, turno, composición e inventario antes de confirmar.
3. `Completed`: venta, stock, deuda, caja y diario ya se confirmaron. Repetir la recuperación es un no-op.
4. `RequiresReconciliation`: el resultado o su registro requieren revisión. El arranque conserva la intención para intervención supervisada; no vuelve a cobrar ni ignora el estado para registrar una venta.
5. `Failed`: una intención descartada o rechazada no puede reutilizarse para vender. Si un registro heredado contradice evidencia bancaria persistida, vuelve a la lista de pendientes para conciliación.

Un aviso tardío de fallo se clasifica dentro de una transacción usando los registros
persistidos. No sustituye una venta confirmada por un fallo ni borra incertidumbre
porque la interfaz haya enviado un indicador distinto. Un pendiente bloquea otro
cobro y el cierre del mismo turno; la intención original conserva su identidad.
Detener la espera local no equivale a cancelar el pago en el proveedor.

La recuperación se ejecuta después de migrar ambas bases y antes de permitir que el arranque continúe. Cualquier fallo de migración sigue siendo fatal.

## Verificación automatizada

La suite `Stage1TransactionalCoreTests` cubre:

- migración desde cada versión comercial soportada conservando importes;
- adopción y actualización de una base comercial sin historial EF;
- repetición de ventas, devoluciones, cancelaciones y abonos;
- competencia por la última unidad de inventario;
- rollback completo por stock insuficiente y por abono superior a la deuda;
- recuperación única después de autorización de terminal;
- reconciliación coherente de caja después de venta y cancelación.

Comandos de aceptación local:

```powershell
dotnet restore Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj -p:RuntimeIdentifier=win-x64
dotnet test Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj --configuration Release --no-restore -p:RuntimeIdentifier=win-x64
dotnet build Ticketfy.Desktop/Ticketfy.Desktop.csproj --configuration Release --no-restore
dotnet list Ticketfy.Desktop/Ticketfy.Desktop.csproj package --vulnerable --include-transitive
```

## Regla para cambios posteriores

Cualquier nueva operación que cambie dinero, inventario, deuda o folios debe incluir, en el mismo cambio:

1. una clave idempotente persistida con índice único;
2. una transacción única para todas sus consecuencias locales;
3. una entrada de `transaction_journal` dentro de esa transacción;
4. una prueba de repetición y una prueba de rollback;
5. una migración versionada cuando cambie el modelo.
