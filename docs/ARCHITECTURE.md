# Arquitectura de Ticketfy

## Estado y plataforma

Ticketfy es un punto de venta de escritorio en desarrollo, con .NET 10 LTS, C#,
Avalonia, CommunityToolkit.Mvvm y SQLite/SQLCipher mediante EF Core 10. Esta
descripción documenta la implementación; no certifica madurez comercial.

El SDK y la herramienta EF están fijados en `global.json` y
`.config/dotnet-tools.json`. El [procedimiento de migración](NET10_MIGRATION.md)
describe la validación aislada y el empaquetado autocontenido.

La aplicación funciona con persistencia local. Facturama, distribución remota y
servidor siguen aplazados. No existe una sincronización multi-sucursal certificada.
La evidencia y los límites de la base comercial están en
[Etapa 6](STAGE_6_COMMERCIAL_FOUNDATION.md).

## Estructura y responsabilidades

| Capa | Responsabilidad |
|---|---|
| Core | Permisos, contratos auxiliares, configuración, mensajes y protocolos |
| Data | Entidades, contexto EF, conversiones monetarias y de cantidades |
| Migrations | Esquema versionado, saldos iniciales y protecciones SQLite |
| Services | Reglas comerciales, transacciones, recuperación e integraciones |
| ViewModels / Views | Formularios, estado observable, comandos y presentación |
| Tests / eng | Regresión, UI headless, cobertura y puerta de calidad |

Se utiliza una fábrica de contextos para los servicios financieros: cada comando
abre su contexto y su transacción. La composición principal permanece en
MainWindowViewModel; todavía existen módulos heredados con contexto compartido,
consultas no paginadas y referencias a controles desde ViewModels. Separarlos y
medir grandes catálogos es trabajo pendiente, no una propiedad ya garantizada.

## Unidad transaccional comercial

1. CommercialOperationContext verifica sesión, usuario vigente, permiso,
   caja y propiedad del turno.
2. CommandIdentity vincula una clave estable a comando, actor y contenido.
   Repetir la clave con un contenido distinto se rechaza.
3. El servicio valida precios, pagos, existencias y saldos en backend.
4. La transacción confirma documento, partidas, pagos, Kardex, cuentas,
   movimientos de caja, recibo de idempotencia y diario.
5. Impresión y callbacks de interfaz ocurren después del commit y no pueden
   justificar repetir el cobro.

El efectivo, tarjeta, transferencia, crédito, monedero y lealtad se contabilizan
por separado. El dinero se calcula en decimal y se persiste con conversiones
explícitas. Algunas cantidades de DTO heredadas siguen siendo double; el límite
transaccional exige cantidades válidas y precisión de tres decimales.

Una devolución agrega ReturnLine y PaymentRefund. No reescribe el importe
original de una venta. Las devoluciones bancarias requieren conciliación:
registrar la intención local no equivale a que el banco haya devuelto dinero.

## Inmutabilidad y concurrencia

SaveChanges impide editar/eliminar registros de ledger. La migración añade
triggers para protegerlos también frente a SQL directo. Son controles de
integridad de aplicación, no protección absoluta frente a un administrador que
pueda modificar el archivo o eliminar los triggers.

StockVersion se compara al descontar o ajustar inventario. El índice de turnos
impide dos aperturas simultáneas en una caja. El cierre, arqueo y diario se guardan
atómicamente y se comprueban contra documentos de efectivo, no sólo contra un total.

ShiftLedgerValidator reúne las reglas compartidas por cierre y conciliación
diaria. Contrasta pagos con venta/operación/actor, efectivo con sus documentos y
comandos, y saldo proyectado con movimientos. Un saldo inconsistente bloquea el
cierre; no se repara silenciosamente. La revisión posterior comprueba además
arqueo final, diario y comando de cierre, y devuelve causas identificables.

La conciliación diaria lee una instantánea transaccional. Comprueba integridad
interna del turno, no existencia física de dinero ni conciliación bancaria o de
inventario; esos controles permanecen en el procedimiento operativo.

## Recuperación y migraciones

CheckoutOperation se persiste antes de solicitar el pago externo. Un cargo
confirmado puede recuperarse sin volver a llamar al cobro. El efectivo incierto y
los cobros mixtos interrumpidos requieren revisión explícita de fondos. El proceso
no inventa evidencia bancaria ni atribuye operaciones a un usuario genérico.

Antes de migrar se genera un respaldo verificado. La migración comercial crea
aperturas de saldos conocidos, pero no reconstruye transacciones históricas
ausentes. No se permite revertir esa migración descartando ledgers: la
contingencia es restaurar el respaldo anterior con conciliación de movimientos.

HistoricalReconciliationService incorpora ajustes explícitos del saldo actual.
Una huella del documento, saldos, asientos y revisiones invalida vistas previas
obsoletas. El acta y el ajuste se guardan juntos; no crean efectivo ni inventario.
El saldo de proveedor se concilia por compra antigua, no por un total sin documento.

IncomingTransferService separa el intento, la comprobación del depósito y su
aplicación. TenderConfirmation persiste antes del abono/recarga; el documento,
asiento de cuenta y IncomingTransferReceipt se confirman en la misma transacción.
Una referencia consumida no puede aplicarse a otro destino. Los intentos sin
resolución impiden cerrar el turno; se recuperan con el operador original o se
resuelven documentando ausencia de fondos o devolución bancaria efectiva.

## Periféricos

La impresión usa un único destino configurado, con cola local de ejecución y
tiempos de espera. No cambia silenciosamente de impresora ni reintenta
automáticamente trabajos cuyo resultado físico es incierto.

El panel Periféricos consulta colas y puertos de Windows cada 30 segundos. No envía
pulsos ni impresiones de prueba. Una cola instalada no prueba conexión física; un
puerto no identifica la báscula; un lector HID puede ser indistinguible de un
teclado; un cajón sin sensor no informa apertura. Los avisos distinguen estas
limitaciones. No existe una cola durable de reimpresión automática.

## Verificación pendiente para comercialización

Además de la suite automatizada, hacen falta una actualización ensayada sobre
copias de bases comerciales representativas, pruebas de carga, accesibilidad,
seguridad independiente, firma de distribución y piloto con conciliación diaria.
La aprobación de proveedores de pago, SAT y hardware exige evidencia real separada.
