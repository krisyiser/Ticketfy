# Auditoría de preparación empresarial de Ticketfy

Fecha de corte: 19 de octubre de 2026. Código funcional evaluado: `a4ce3b78cfb438102fe19b585ff368ee36412935`. Candidato final en `origin/main`: `428dd01c67f2a5bd7d0a5b36d75b73b88fd6df0e`, que sólo corrige la prueba de zona horaria.

## Dictamen

Esta auditoría conserva el diagnóstico del candidato indicado arriba. Las correcciones posteriores, sus pruebas y los límites de aprobación se siguen en [el registro de remediación](ENTERPRISE_REMEDIATION_PROGRESS.md); no interpretar los hallazgos originales como una segunda revisión independiente de los cambios nuevos.

Ticketfy sigue en **Etapa 6, estabilización de una beta interna**. La migración a .NET 10 y los ledgers mejoran sustancialmente la base técnica, pero no convierten al sistema en una versión empresarial aprobada. El próximo entregable debe ser un candidato local para piloto, con alcance explícito y procedimientos completos de recuperación.

Hay bloqueos funcionales y de continuidad que pueden persistir aunque las 202 pruebas actuales pasen. Destacan la resolución de cobros por cajeros, recuperación ante pérdida del perfil Windows, saldos a favor de clientes/proveedores y autorización incompleta de lecturas. No se recomienda operar dinero real sin resolver o excluir formalmente esos escenarios.

Alcance actual: aplicación local Windows, con datos sintéticos. Facturama, servidores, sitio de descargas y Forgejo permanecen desactivados o aplazados. La publicación de fuentes en GitHub no despliega el POS ni migra una base. No se abrió la base operativa ni se ejecutaron cobros o instalaciones reales.

La revisión es interna: combina inspección de servicios, modelos, interfaz, pruebas, configuración y documentación con el gate automatizado. Los escenarios nuevos descritos abajo son deducciones del código, salvo donde se cita una prueba existente; no se presentan como incidentes reales ni como una auditoría independiente de seguridad.

## Evidencia del candidato

| Comprobación | Resultado |
|---|---|
| Commit y publicación | `a4ce3b7` publicado en GitHub, rama `main` |
| Gate local con `-LockedRestore -RequireClean -VerifyReproducible` | Aprobado sobre el commit limpio |
| Suite Release | 202 aprobadas, ninguna fallida ni omitida |
| Cobertura crítica | 87,66%, 2835 de 3234 líneas en 26 archivos; no es cobertura total del producto |
| Compilación Desktop, pruebas y Keygen en el gate | Sin advertencias ni errores |
| Modelos comercial y auditoría | Sin diferencias pendientes respecto de las migraciones |
| Dependencias directas y transitivas | Sin vulnerabilidades conocidas reportadas por NuGet en esta ejecución |
| Dos paquetes locales del commit limpio | SHA256 idéntico; paquetes sin firma |
| Primera CI de GitHub | [Run del candidato](https://github.com/krisyiser/Ticketfy/actions/runs/39220245581): 201 pruebas aprobadas y una fallida por asumir la zona horaria del equipo; seguimiento de la corrección al final |
| CI final de GitHub | [Run 39221372048](https://github.com/krisyiser/Ticketfy/actions/runs/39221372048) aprobado para `428dd01`; 202 pruebas, 87,66% de cobertura crítica y paquetes reproducibles |

Evidencia local: `artifacts/quality-5bd14accf27a4f66b3b4768ccba1d9b7/TestResults/quality.trx`, cobertura en su subdirectorio y auditorías `*-dependencies.json`. SHA256 de ambos paquetes: `BBF110E9E9A12C47117899247D843B32DE852859B101058A690215FD9A0AC53D`.

El gate verde acredita los escenarios automatizados y la repetición del empaquetado en ese entorno. No acredita firma, instalación, comportamiento bancario, recuperación física del equipo ni paridad con una edición concreta de eleventa, SICAR o Aspel Caja.

## Estado de los flujos comerciales

| Flujo | Base implementada | Brecha relevante |
|---|---|---|
| Alta e importación de productos | Validación, código único, baja lógica, versión de stock, ajustes con motivo y Kardex | Importaciones voluminosas, normalización y escenarios de conversión de unidades necesitan aceptación sistemática |
| Promociones | Alta/edición, vigencias, prioridad, aplicación al carrito y revalidación al cobrar | Trazabilidad del alta sin ID; reglas límite y selección de mejor oferta requieren contrato funcional explícito |
| Clientes y crédito | Alta, límites, deuda transaccional, abonos y estado de cuenta | Consulta sin autorización en algunas rutas; crédito ya pagado que luego se devuelve; tarjeta no admitida para abonos |
| Apertura y venta | Usuario/caja/turno, pagos separados, total exacto, stock y diario en transacción | Recuperación con roles normales; aceptación de fallos reales de proceso y equipo |
| Tarjeta y transferencia | Referencia persistida, verificación de importes y conciliación; transferencia manual | Terminal no homologada; reembolso bancario conciliado manualmente, no ejecutado por el adaptador |
| Devoluciones | Partidas, costos históricos, restauración de componentes y medios originales | Documentos heredados sin desglose bloqueados; saldos a favor sin ciclo completo de liquidación |
| Compras y proveedores | Contado/crédito, pagos, recepción de stock y devolución | Crédito del proveedor sin aplicación explícita a otra compra ni cobro de reembolso |
| Gastos | Registro idempotente, cancelación con motivo y recuperación confirmada | Medio de pago acepta cadenas no catalogadas si contienen referencia |
| Cierre y conciliación | Arqueo atómico y comprobaciones documentales compartidas | Conciliación interna no equivale a banco, inventario físico ni totalidad de cuentas |
| Historial y reportes | Ventas, costos, devoluciones y pagos persistidos | Lecturas financieras sin snapshot común y consultas completas/N+1 |
| Respaldo | SQLite Backup API, retención, verificación y preparación de restauración | Recuperación integral de auditoría y llaves ante pérdida del equipo |
| Periféricos | Detección pasiva y mensajes de resultado de impresión | Ningún modelo certificado físicamente; detección no equivale a impresión o apertura real |

## Hallazgos prioritarios

P1 indica un bloqueo para aprobar el escenario afectado en un piloto comercial. P2 indica una corrección necesaria para la versión comercial o su escala prevista. Son prioridades del producto, no puntuaciones CVSS.

### H01 Recuperación de cobros incompatible con el rol de cajero

**P1, confirmado por inspección de las condiciones de autorización.** En [SaleService.Recovery](../Ticketfy.Desktop/Services/Implementations/SaleService.Recovery.cs), `ResolveInterruptedCheckoutAsync` exige `PaymentsReconcile` y después exige exactamente usuario, caja y turno originales. El [catálogo de permisos](../Ticketfy.Desktop/Core/Security/PermissionCatalog.cs) no concede ese permiso a CAJERO ni SUPERVISOR por defecto. Un gerente autorizado no satisface la identidad original.

Un cobro incierto iniciado por un cajero puede impedir el cierre y quedar sin una resolución ordinaria. Conceder temporalmente permisos o compartir credenciales no debe ser el procedimiento de soporte.

Salida: separar al operador original del autorizador de la recuperación, conservar ambas identidades y permitir aprobación supervisada sin reasignar la venta. Probar cajero sin permiso, gerente distinto, usuario original inactivo, reinicio y resolución repetida, sin nuevo cargo ni duplicación de stock.

### H02 El respaldo no resuelve la pérdida completa del equipo

**P1, brecha de continuidad confirmada en el alcance implementado.** [BackupService](../Ticketfy.Desktop/Services/Implementations/BackupService.cs) respalda una base comercial y usa un proveedor de contraseña. [AppBootstrapper](../Ticketfy.Desktop/Core/Startup/AppBootstrapper.cs) no compone un respaldo integral de la base de auditoría y llaves. [AppDataPaths](../Ticketfy.Desktop/Core/Configuration/AppDataPaths.cs) mantiene por separado base de auditoría, llave de base y llave de integridad. [DpapiSecretStore](../Ticketfy.Desktop/Services/Security/DpapiSecretStore.cs) protege secretos bajo el usuario Windows.

El ensayo actual de restauración en el mismo perfil no demuestra que un respaldo sea recuperable después de perder ese perfil/equipo. DPAPI normalmente requiere las credenciales y el equipo de origen, con excepciones de perfiles itinerantes; copiar el archivo protegido no constituye un plan de recuperación. [Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).

Salida: diseñar custodia recuperable y cifrada de llaves, inventario del conjunto de recuperación y restauración coherente de datos/auditoría. Ensayar en otro perfil y equipo de laboratorio, sin usar secretos reales. No exportar llaves en texto plano. Definir RPO y RTO y medirlos.

### H03 Devolver una venta a crédito ya pagada deja un saldo a favor sin liquidación completa

**P1 si se ofrece devolución de crédito liquidado.** [SaleService.Cancellation](../Ticketfy.Desktop/Services/Implementations/SaleService.Cancellation.cs) resta el importe del pago original de crédito a `customers.debt` y añade `RETURN_CREDIT`, independientemente de los abonos posteriores. Una venta de 100, abonada en 100 y devuelta en 100, conduce por esa lógica a deuda de -100. Esto puede representar legítimamente un saldo a favor, pero no equivale a devolver efectivo.

[CustomerService](../Ticketfy.Desktop/Services/Implementations/CustomerService.cs) aplica abonos a documentos positivos, mientras el crédito futuro se controla contra el saldo global. No hay un ciclo explícito de liquidación/reembolso de ese saldo a favor ni una asignación compensatoria entre documentos en las interfaces revisadas. La prueba `DiscountedCreditReturn_RestoresProratedDebtWithoutRewritingSale` cubre devolución antes de abonar, no esta secuencia.

Salida: definir saldo deudor y acreedor por documento; registrar aplicación o reembolso con identidad idempotente y medio verificado. Probar abonos parciales/totales, devolución parcial/total, otros documentos pendientes y repetición. El resultado debe explicar dónde quedó cada peso, no sólo cuadrar el saldo global.

### H04 El crédito del proveedor no tiene un ciclo completo de recuperación

**P1 para compras/devoluciones de alcance completo.** [PurchaseService.ReturnAsync](../Ticketfy.Desktop/Services/Implementations/PurchaseService.cs) descuenta inventario y crea crédito en la cuenta del proveedor, pero no registra dinero recibido. La interfaz lo comunica correctamente. La prueba `PurchaseCashCreditSupplierPaymentAndReturn_Reconcile` confirma un saldo de -4 después de devolver una partida de una compra liquidada.

`IPurchaseService` ofrece pagar una compra y devolver mercancía, no aplicar ese crédito a otra compra o registrar un reembolso recibido enlazado a la devolución. Un ingreso manual de caja no resuelve la trazabilidad de la cuenta por pagar.

Salida: aplicación de nota de crédito y recepción de reembolso por medio, con asiento vinculado, límites y clave idempotente. Aceptación: compra pagada, devolución, compensación o reembolso y estado final cero sin cambiar documentos originales.

### H05 RBAC incompleto en lecturas y servicios heredados

**P1 de autorización para una distribución empresarial.** `CustomerService.GetAllAsync`, `GetByIdAsync` y `GetFiscalDataAsync` no invocan autorización; tampoco las lecturas equivalentes de [SupplierService](../Ticketfy.Desktop/Services/Implementations/SupplierService.cs) y algunas lecturas de producto/venta. La existencia de permisos en navegación no protege una llamada directa al servicio. Esto no demuestra un ataque remoto: el límite vulnerable es el contrato interno de acceso.

[AttendanceService](../Ticketfy.Desktop/Services/Implementations/AttendanceService.cs) recibe un identificador de usuario y [ShiftNoteService](../Ticketfy.Desktop/Services/Implementations/ShiftNoteService.cs) permite editar/eliminar notas sin el contexto central de permisos ni diario administrativo en esos métodos.

Salida: matriz de consultas y comandos por rol; separar el catálogo mínimo necesario para cobrar de costos, deuda y datos personales. Pruebas de servicio sin sesión, sesión bloqueada y rol denegado; revisión de exportaciones y operaciones heredadas. No basta con esconder botones.

### H06 Los reportes financieros pueden mezclar instantes de lectura

**P1 para reportes usados durante operación concurrente; riesgo deducido del código.** [ExpenseService.TotalsAsync](../Ticketfy.Desktop/Services/Implementations/ExpenseService.cs) lee ventas, devoluciones, gastos, pagos y liquidaciones en consultas sucesivas sin transacción de lectura común. Una venta confirmada entre consultas puede aparecer en cobros pero no en ventas del mismo informe. La conciliación de turnos ya usa una transacción consistente y ofrece un patrón para corregirlo.

Salida: consultas con un corte consistente y definiciones documentadas de venta, cobro, devolución, saldo y utilidad. Inyectar escrituras entre lecturas en una regresión y exigir que el informe represente un único estado. Definir además el tratamiento de impuestos, costos y comisiones antes de llamar al indicador utilidad contable; no confundir margen comercial con una contabilidad empresarial completa.

### H07 Asistencia todavía aplica una regla global a múltiples cajas

**P2, condición incorrecta confirmada.** `AttendanceService.ClockOutAsync` busca cualquier turno abierto, sin filtrar por el usuario que sale. Si A terminó su turno y B sigue trabajando, la salida de A es rechazada. La prueba de cierre multicaja en `ShiftService` no cubre este servicio.

Salida: asociar asistencia/turnos a la identidad correspondiente y probar dos usuarios/cajas, ambos órdenes de salida, turnos históricos y concurrencia. No usar registros de asistencia con anomalías como horas confirmadas para productividad sin una política explícita.

### H08 Reportes y catálogos cargan historiales completos

**P2, patrón confirmado; latencias aún no medidas.** `PurchaseService.GetAllAsync` carga todas las compras y partidas, y después cruza las listas en memoria. `SaleService.GetSalesByDateRangeAsync`, `ExpenseService` y `PerformanceAnalyticsService` cargan tablas completas antes de filtrar. `SaleService.GetHistoryAsync` realiza consultas adicionales por cada venta en `MapWithLedgersAsync`. Los kits también agregan consultas recursivas.

Salida: filtros e índices adecuados, proyecciones y paginación, eliminando consultas por documento donde proceda. Medir búsqueda, cobro y reportes con datos sintéticos representativos. No afirmar soporte para millones de registros sólo porque SQLite los almacena.

### H09 El contrato de gastos admite medios desconocidos

**P2, validación incompleta confirmada.** `ExpenseService.CreateAsync` distingue efectivo frente al resto y exige referencia para lo no efectivo, pero no valida una lista cerrada. Un valor arbitrario puede quedar registrado como gasto no efectivo y omitirse del movimiento de caja.

Salida: medio tipado/catálogo central y rechazo de valores desconocidos en backend. Probar efectivo, tarjeta, transferencia, valores inválidos y cancelaciones por cada medio. La UI no sustituye esta validación.

### H10 El alta de promociones sin ID pierde la referencia del documento en el diario

**P2, defecto localizado confirmado.** `PromotionService.SaveAsync` genera un ID cuando la entrada está vacía, pero escribe `PROMOTION_SAVED` con `promotion.Id` en lugar del ID generado. La promoción se conserva, pero el evento puede tener documento vacío. El flujo normal de pantalla con ID previamente creado puede ocultar el caso.

Salida: usar la identidad persistida en el diario y agregar una regresión de alta sin ID; exigir referencia resoluble en cada evento administrativo.

### H11 Las notas libres pueden incluir datos personales en logs

**P2, exposición potencial confirmada por el punto de logging.** `ShiftNoteService.SaveNoteAsync` registra el texto completo de la nota y el nombre recibido. [LogSanitizer](../Ticketfy.Desktop/Logging/LogSanitizer.cs) elimina varios patrones de secretos y datos personales, pero no identifica nombres, direcciones ni todo el contenido libre. Por ejemplo, un nombre con un teléfono de diez dígitos puede permanecer.

Salida: no registrar texto libre operativo; usar identificador de nota, actor y resultado. Probar logs y diagnóstico exportado con información sintética sensible. Revisar retención y accesos, sin atribuir una certificación legal al sanitizador.

### H12 La calificación comercial sigue incompleta

**P1 de aprobación de salida, no un fallo de compilación.** La UI automatizada usa Avalonia headless y ejecuta comandos enlazados; no es un ensayo completo del ejecutable instalado con lector, impresora, terminal y apagado físico. Las migraciones recorren esquemas sintéticos, no copias representativas autorizadas de instalaciones. El paquete del gate no está firmado y el workflow no instala/actualiza el producto en una VM limpia. El [workflow actual](../.github/workflows/quality.yml) conserva `TestResults`, pero no sube los ZIP generados como artefactos descargables: construirlos no equivale a disponer de un canal de entrega del candidato.

Salida: pruebas de instalación/actualización/recuperación, hardware por modelo, cuentas de prueba autorizadas, seguridad independiente y piloto conciliado. La excepción de cobertura del inicializador debe tratarse con pruebas específicas de arranque, no esconderse tras el porcentaje agregado.

### H13 El día comercial depende de la configuración horaria de Windows

**P2, dependencia confirmada; fallo de prueba reproducido en CI.** [DateTimeExtensions](../Ticketfy.Desktop/Core/Helpers/DateTimeExtensions.cs) usa `ToLocalTime`, `ToUniversalTime` y `LocalDateTime`, no una zona explícita del negocio. La prueba anterior suponía Ciudad de México: el mismo instante produjo día 10 en el runner y día 9 en la expectativa. La CI lo detectó y detuvo la publicación del paquete.

La corrección de CI hace que la prueba verifique el contrato vigente contra `TimeZoneInfo.Local`, con invierno/verano, límites de día y conversión de ida/vuelta. No cambia fechas almacenadas ni convierte silenciosamente todo el producto a una zona fija. Queda pendiente definir la zona del negocio y probar el mismo dataset bajo distintas zonas de Windows, incluyendo horarios heredados sin offset y cortes nocturnos. Ese contrato es necesario antes de ofrecer resultados consistentes entre instalaciones.

## Arquitectura y límites del alcance empresarial

No hace falta reescribir todo el POS. El patrón de servicios transaccionales con factoría de contextos, identidad de operación y asientos compensatorios debe conservarse. El desacoplamiento prioritario es retirar el contexto compartido y la construcción manual extensa de [MainWindowViewModel](../Ticketfy.Desktop/ViewModels/MainWindowViewModel.cs), empezando por asistencia, notas y rutas con acceso directo a datos. Cada extracción debe mantener las regresiones monetarias.

Una edición empresarial local y una plataforma multisucursal no son el mismo entregable. Para la primera se requiere continuidad, autorización, trazabilidad y soporte dentro de un alcance explícito. Para múltiples equipos con datos compartidos se debe diseñar un servicio de aplicación y contratos de concurrencia/sincronización; no publicar una carpeta SQLite compartida como solución. SQLite desaconseja el acceso directo simultáneo desde muchos equipos por red y sólo admite un escritor a la vez por base. [SQLite](https://www.sqlite.org/whentouse.html).

La [ruta de implementación](ENTERPRISE_ROADMAP.md) separa estos alcances y sus puertas de aceptación. Facturama permanece fuera de ambos hasta recibir autorización para retomarlo.

## Cierre de la validación remota

El commit `428dd01` corrige exclusivamente la prueba dependiente de la zona del equipo. La suite local posterior aprobó 202 casos y conserva su TRX en `artifacts/ci-timezone-followup/regression.trx`.

GitHub Actions terminó con `success` en el [run 39221372048](https://github.com/krisyiser/Ticketfy/actions/runs/39221372048), sobre ese mismo SHA. El log confirmó 202 aprobadas, cero fallidas/omitidas, 87,66% de cobertura crítica y compilaciones sin advertencias ni errores. El gate remoto también verificó formato, modelos, dependencias y limpieza del checkout.

Los dos paquetes del runner coincidieron en SHA256 `518CD2C8C42A69B5C85B3576B8B38CED6EAD8560B28778F39DE8AEF5934C78CA`. Este hash pertenece al candidato final remoto, no al paquete local del commit anterior. Log descargado: `artifacts/github-ci-39221372048.log`.

La auditoría y la ruta se guardan como documentos locales nuevos, sin commit ni publicación en esta revisión. Los hallazgos H01–H12 no se corrigieron como parte del encargo de auditoría; H13 conserva pendiente el contrato de zona del negocio aunque su prueba ya es portátil. Facturama permanece desactivado y la base operativa no se tocó.
