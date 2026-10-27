# Ruta de Ticketfy hacia una versión empresarial

Plan propuesto el 19 de octubre de 2026, basado en la [auditoría del candidato a4ce3b7](ENTERPRISE_READINESS_AUDIT.md). El objetivo inmediato es una edición local confiable para un piloto acotado, no declarar paridad general con otros POS ni habilitar infraestructura aplazada.

## Reglas de trabajo

- Utilizar bases sintéticas y llaves de prueba. La base operativa no se abre, migra ni restaura sin autorización y respaldo verificado.
- Mantener Facturama, Forgejo y servicios remotos aplazados. GitHub conserva fuentes y CI; no activa una distribución comercial.
- Por cada defecto: escenario reproducible, prueba que falle antes del arreglo, corrección, regresión verde y evidencia del commit.
- Ningún ajuste directo a una venta publicada para cuadrar resultados. Utilizar operaciones compensatorias con documento, motivo, actor, caja, turno e idempotencia.
- Separar lo implementado, lo validado automáticamente, lo probado por operador y lo certificado físicamente.

## Secuencia y puertas de aceptación

| Hito | Trabajo prioritario | Dependencia | Salida verificable |
|---|---|---|---|
| A Candidato reproducible | CI del mismo commit, artefacto trazable y manifiesto; mantener dependencias fijadas | Commit publicado | Gate verde y evidencia conservada; aún sin aprobación comercial |
| B Corrección funcional y autorización | H01, H03–H07, H09–H11 y H13; recuperación supervisada, saldos a favor, RBAC, reportes y día comercial | A | Matriz de escenarios nuevos verde, cero diferencias inexplicadas y ningún P1 abierto en los flujos habilitados |
| C Continuidad de negocio | H02; recuperación de datos, auditoría y llaves; actualización reversible | A, cierre de contratos de datos de B | Restauración en otro perfil/equipo de prueba y conciliación de saldos/documentos dentro de RPO/RTO acordados |
| D Rendimiento y mantenibilidad | H08; paginación, consultas, composición y contextos; ampliar arranque/UI/fallos | B | Métricas aprobadas con volumen acordado; ningún retroceso monetario ni de permisos |
| E Candidato instalable | Firma, instalación/actualización en VM, matriz Windows y manuales ejecutados por otra persona | B, C, D | Instalador del mismo commit aprobado, firma verificada y contingencia ensayada |
| F Laboratorio y piloto | Hardware/cuentas autorizados, operador, conciliación diaria y soporte | E y disponibilidad externa | 2–4 semanas de piloto sin duplicaciones, pérdida de trazabilidad ni diferencias sin resolver |
| G Primera edición comercial local | Seguridad independiente, alcance publicado, soporte y aceptación del propietario | F | Aprobación formal de salida; sin hallazgos altos/críticos abiertos en el alcance ofrecido |

El hito A no cierra B–G. La ausencia actual de hardware no impide B–D ni preparar E; sí impide certificar F.

## Contratos funcionales que deben cerrarse

Antes del hito E, cada prestación debe estar completa y probada o excluida expresamente de la edición ofrecida:

- Abonos de clientes y recargas con tarjeta: hoy están restringidos. Si se incluyen, necesitan intención persistida, referencia bancaria, recuperación, rechazo y devolución, no sólo un selector de medio.
- Devoluciones históricas: incorporar documentos verificables mediante un procedimiento asistido o mantener la restricción con un procedimiento de atención documentado. Conciliar un saldo no crea las partidas ni costos faltantes.
- Proveedores: especificar notas de crédito, reembolsos recibidos, aplicación a otras compras y vencimientos; no presentar el saldo global como liquidación documental.
- Inventario: aprobar reglas de unidades y fracciones, kits, importación, mínimos/máximos, conteo físico, ajustes y valorización. Registrar separadamente merma y devolución sin reposición.
- Reportes: definir periodo/zona, impuestos incluidos, costo histórico, cobros versus ingresos, devoluciones, comisiones y saldos pendientes. Conciliación bancaria y utilidad contable requieren contratos adicionales al reporte comercial actual.
- Instalación local: declarar versiones/arquitecturas de Windows y periféricos realmente aprobados; excluir configuraciones no ensayadas. La publicación x86 por sí sola no habilita soporte x86.

La matriz comparativa con eleventa, SICAR y Aspel debe indicar ediciones concretas y necesidades del comercio. No utilizar una lista de marcas como requisito de paridad indefinida ni prometer todas sus funciones en esta primera edición.

## Primer ciclo de implementación

Ejecución iniciada: consultar [remediación y evidencia](ENTERPRISE_REMEDIATION_PROGRESS.md) para el estado actual. La lista siguiente conserva la priorización original; no es una declaración de que esos cambios sigan sin implementar. El [runbook de recuperación](ENTERPRISE_RECOVERY_RUNBOOK.md) describe los procedimientos nuevos y las pruebas externas todavía pendientes.

Prioridad sugerida para el siguiente trabajo autorizado:

1. Reproducir H01 con un CAJERO real del catálogo de permisos y un GERENTE distinto; diseñar autorización supervisada sin suplantación.
2. Cubrir H03/H04 con venta o compra completamente pagada antes de devolver. Acordar aplicación del saldo a favor frente a reembolso; no inferir recepción de dinero.
3. Aplicar lectura consistente a informes y agregar una prueba que escriba entre consultas.
4. Completar RBAC de lecturas, asistencia y notas; corregir el filtro de salida por usuario/caja.
5. Corregir catálogo de medios del gasto, referencia de auditoría de promoción y logs de notas.
6. Diseñar el conjunto de recuperación cifrado y probarlo en un perfil desechable. No modificar las llaves del operador.

Responsables propuestos: desarrollo para cambios y regresiones; responsable del negocio para políticas de crédito/reembolso; QA u operador distinto del desarrollador para aceptación. Estos roles requieren asignación, no se presupone que ya existan recursos dedicados.

## Matriz mínima de aceptación del dinero

Para cada escenario comprobar documento, partidas, pagos, devolución, inventario, cuenta del cliente/proveedor, caja, turno y diario. Cuando un registro no corresponda, documentar por qué debe permanecer sin cambio.

| Escenario | Variantes mínimas | Invariante principal |
|---|---|---|
| Venta | Efectivo, tarjeta, transferencia, crédito, monedero, mixto | Pagos aceptados iguales al total; efectivo neto igual al movimiento de caja |
| Repetición | Doble clic, respuesta perdida, reinicio, dos procesos | Una operación y un folio; misma clave con contenido distinto rechazada |
| Devolución | Parcial/total, con/sin reposición, kit modificado, distintos turnos | No excede lo vendido/pagado; costo y componentes históricos preservados |
| Crédito de cliente | Sin abonos, abono parcial, liquidado, otros documentos abiertos | Deuda y saldo a favor explicables por documento; aplicación/reembolso no duplica dinero |
| Compra | Contado, crédito, pago parcial, devolución posterior al pago | Stock y cuenta correctos; crédito del proveedor liquidable con evidencia |
| Recuperación | Cajero original, gerente aprobador, usuario inactivo, fondos inciertos | Nunca volver a cobrar automáticamente; identidades separadas y evidencia durable |
| Caja | Dos cajas, retiros, gastos, cancelaciones, cierre interrumpido | No mezcla turnos; diferencia real visible; cierre completo o no aplicado |
| Reportes | Escritura concurrente, medianoche, devolución posterior a venta | Corte consistente y periodo comercial explícito |
| Fallos | Disco lleno, bloqueo de DB, proceso terminado, red intermitente | No confirmar éxito antes del commit; recuperación sin edición manual de DB |

Los fallos inyectados no sustituyen los ensayos posteriores de corte eléctrico y periféricos reales. Los documentos históricos incompletos deben bloquearse con instrucciones de conciliación o incorporarse mediante un procedimiento validado; nunca reconstruir costos o pagos ficticios.

## Presupuestos de rendimiento propuestos

Acordar primero equipo de referencia, SSD, RAM, versión de Windows, tamaño de catálogo y años de historial. Usar conjuntos sintéticos crecientes de 10 mil y 100 mil productos, y de 100 mil a un millón de ventas, incluyendo devoluciones y pagos mixtos. Son cargas de evaluación, no capacidades certificadas.

Objetivos iniciales para discutir: búsqueda p95 menor a 300 ms, confirmación local de venta p95 menor a 1 s excluyendo banco/impresión, primera página de historial menor a 1 s e informe diario menor a 3 s. Registrar también p99, memoria, tiempo bloqueado y errores; no aprobar por un promedio aislado. Si no se cumplen, ajustar implementación o alcance publicado según mediciones.

Para continuidad, el procedimiento integral inicial es manual, con turnos cerrados: propuesta de RPO máximo de 24 horas con copia diaria y RTO de 4 horas, todavía no medidos ni comprometidos. Los backups automáticos de la base no sustituyen el paquete integral de llaves y auditoría. Alcanzar una hora de RPO/RTO requerirá ampliar y ensayar ese procedimiento; no es una capacidad certificada. Medir restauración integral y definir cómo conciliar pagos bancarios posteriores al respaldo antes de reabrir.

## Seguridad y operación antes del piloto

- Revisar permisos negativos por rol en servicios y UI; autorizaciones excepcionales con actor original y aprobador.
- Completar recuperación de llaves y auditoría; separar custodia, permisos de exportación y procedimientos de emergencia.
- Mantener escaneo de dependencias y añadir revisión independiente de amenazas, secretos, instalador y mecanismos de actualización.
- Incorporar instalación, actualización, arranque fallido y restauración a la validación en Windows desechable.
- Revisar privacidad, retención y obligaciones fiscales aplicables con especialistas antes de ofrecer esas prestaciones. Facturama no se incluye como función terminada.
- Ensayar manuales de apertura, venta, devolución, corte, recuperación e impresión con un operador que no tenga acceso al código ni a SQL.
- Establecer canal de soporte, severidades, responsable, tiempos de respuesta y seguimiento de versiones. Un compromiso de servicio sólo se publica cuando pueda sostenerse.

## Duración y alcance

Estimación de planificación, no compromiso: con un desarrollador dedicado, apoyo de QA/operador y alcance estable, reservar aproximadamente **4–8 semanas** para B–D y preparación de E. Laboratorio, revisión externa, distribución y piloto pueden llevar el total de una primera edición local acotada a **8–14 semanas**, con actividades paralelas. Reestimar después de las primeras regresiones y mediciones; la nueva revisión identifica contratos financieros que todavía deben acordarse.

La madurez empresarial sostenida requiere además varios meses de experiencia de campo y soporte. Multisucursal, administración central, sincronización y alta disponibilidad necesitan otro diseño y presupuesto; no están incluidas en estos rangos. Las demoras de hardware, cuentas, certificados o revisión independiente no se resuelven con más pruebas simuladas.

La decisión de salida se toma por evidencia y alcance, no por porcentaje subjetivo de avance: cero bloqueos P1 en funciones ofrecidas, dinero/stock/folios conciliados, recuperación demostrada, seguridad aprobada, instalador verificado y piloto aceptado.
