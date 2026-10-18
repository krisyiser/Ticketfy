# Madurez comercial de Ticketfy

Fecha de evaluación: 18 de octubre de 2026.

## Dictamen

Ticketfy está en la Etapa 6, en estabilización de una beta interna del núcleo
comercial. Tiene una base transaccional y controles automatizados relevantes,
pero todavía no es un candidato aprobado para operar comercios sin supervisión.
El siguiente hito es un candidato para piloto controlado, no una distribución
empresarial general.

La evaluación considera Windows y persistencia local. Facturama, servidores y
distribución remota siguen aplazados. Las pruebas con varias cajas dentro de una
base temporal no certifican instalaciones distribuidas ni SQLite en una carpeta
compartida. Multisucursal, sincronización y alta disponibilidad requieren un
alcance y diseño separados si van a formar parte de la oferta.

No existe una matriz comparativa aprobada contra ediciones concretas de eleventa,
SICAR o Aspel Caja. Por ello no se afirma paridad funcional ni un porcentaje de
producto terminado. La cobertura de código tampoco mide ese porcentaje.

La validación local de .NET 10 tiene 202 pruebas aprobadas, 0 fallidas/omitidas,
87,66% de cobertura de los 26 archivos críticos y compilación sin advertencias.
Es evidencia de los escenarios automatizados, no aprobación del piloto ni de
seguridad independiente.

## Estado de las etapas

| Etapa | Estado técnico observado | Condición pendiente para salida comercial |
|---|---|---|
| 0 y 1, saneamiento y transacciones | Migraciones, importes decimales, idempotencia, concurrencia de stock y recuperación implementadas | Actualización y restauración sobre copias representativas autorizadas, además de esquemas sintéticos |
| 2, seguridad | DPAPI, contraseñas, RBAC, registros sanitizados y auditoría implementados | Revisión independiente sin hallazgos altos/críticos, firma comercial y revisión de políticas aplicables |
| 3, integraciones | Adaptadores, cola y recuperación probados con dobles | Cuentas y hardware reales; fiscal aplazado, ningún periférico certificado |
| 4, calidad | Suite local, cobertura crítica, formato, análisis y scripts de empaquetado | Commit limpio del candidato actual, CI verde y artefacto reproducible de ese mismo commit |
| 5, operación | Respaldos, restauración guiada, diagnóstico y runbooks | Simulacro operativo y recuperación ante pérdida de equipo/perfil, no sólo en el mismo perfil Windows |
| 6, producto comercial | Ledgers, caja, promociones, compras, devoluciones modernas y conciliación histórica implementados | Completar flujos restringidos, rendimiento, usabilidad y piloto diario conciliado |

Avanzar a una etapa posterior no da por satisfechos los criterios externos de las
anteriores. El estado de ejecución y las métricas actuales se conservan en
[Etapa 6](STAGE_6_COMMERCIAL_FOUNDATION.md).

## Mejora de conciliación de esta revisión

Se unificaron comprobaciones de cierre y conciliación diaria en
`ShiftLedgerValidator`. Ahora se contrastan identidad, estado y documento de los
pagos; respaldo de movimientos manuales y recuperación de gastos; saldo de caja
almacenado frente al calculado; y correspondencia de arqueo, comando y diario de
cierre. Los reembolsos pendientes se comparan por su importe completo.

La pantalla muestra causas de revisión y la caja/usuario reales. Un turno con
fecha ilegible no desaparece del informe: se señala que no puede atribuirse al
día consultado. El informe se lee en una transacción consistente y no modifica
saldos para hacerlos coincidir.

Once escenarios de inconsistencia inyectada no fueron detectados por la versión
anterior en la ejecución de regresión. Esto es evidencia de una laguna de
validación, no de once incidentes ocurridos en un comercio. Las pruebas usan una
base temporal sin los triggers de inmutabilidad para representar datos dañados;
el acceso normal de producción tiene protecciones adicionales.

El resultado «CONCILIADO INTERNO» acredita estas comprobaciones de turno, pagos y
efectivo. No certifica saldo bancario, stock físico, deuda completa del negocio,
CFDI ni integridad frente a un administrador capaz de alterar el archivo.

## Prioridades para el candidato a piloto

1. **Validar el candidato sobre la plataforma LTS.** Desktop, pruebas y Keygen ya
   utilizan .NET 10 y EF Core 10, con SDK, dependencias y CI fijados. Consultar la
   [evidencia y procedimiento de migración](NET10_MIGRATION.md). La actualización
   técnica no sustituye las pruebas de instalación, firma y actualización de una
   copia representativa autorizada. Los parches futuros deben incluirse también
   en el runtime autocontenido del paquete.
2. **Cerrar contratos funcionales.** Abonos/recargas con tarjeta, procedimiento
   para devoluciones de documentos históricos incompletos y revisión de todas
   las rutas de alta, edición, inactivación, pagos y cancelaciones. No suplir
   evidencia faltante con cargos, inventario o usuarios ficticios.
3. **Probar datos y volumen representativos.** Medir búsqueda, cobro, reportes,
   respaldo y recuperación con años de movimientos; reducir cargas completas
   de tablas y consultas por documento. Acordar tamaños y latencias aceptables
   antes de usar el resultado como certificación de rendimiento.
4. **Desacoplar módulos heredados.** Reducir contextos compartidos y composición
   manual en la ventana principal; mantener reglas financieras en servicios y
   validadores comunes, con regresiones por cada extracción.
5. **Completar operación humana.** Validar teclado/lector, mensajes de error,
   reimpresión, estados pendientes, permisos y resolución de incidentes por un
   operador distinto del desarrollador, sin editar la base.

La siguiente entrega no necesita inventar más pantallas: debe demostrar que los
flujos ofrecidos se completan, fallan de forma segura y son recuperables.

## Piloto y aprobación de salida

Antes de operar dinero real deben estar definidos caja, usuarios, copia de datos,
respaldo, límites y responsables. El [guion del piloto](runbooks/COMMERCIAL_PILOT.md)
se ejecuta primero con datos de prueba y después con autorización explícita del
propietario para el entorno real.

Como criterio propuesto, mantener de dos a cuatro semanas de operación controlada
con conciliación diaria de ventas, devoluciones, pagos, efectivo, inventario y
cuentas. Toda diferencia debe tener explicación y resolución documentada. Una
duplicación monetaria o pérdida de trazabilidad impide aprobar la salida aunque
el resto de las pruebas pase.

La aprobación también requiere:

- Recuperar una copia y comprobar saldos, folios y documentos. Ensayar pérdida de
  equipo/perfil y custodia de llaves: copiar un archivo DPAPI no resuelve ese caso.
- Certificar los modelos de periféricos anunciados y los medios de pago ofrecidos.
- Revisar seguridad por un tercero y cerrar hallazgos altos/críticos.
- Acordar soporte, respuesta a incidentes, privacidad y conservación; revisar el
  alcance fiscal antes de ofrecerlo como funcionalidad comercial.
- Generar y verificar instalador firmado desde el mismo commit limpio aprobado,
  con prueba de actualización y contingencia.

## Estimación de trabajo restante

Son rangos de planificación, no fechas comprometidas ni una medición de avance.
Suponen un desarrollador dedicado, apoyo de QA/operador del negocio, alcance
estable y acceso oportuno a equipos, cuentas y revisión externa.

| Hito | Rango orientativo desde el estado actual | Resultado esperado |
|---|---|---|
| Candidato local para piloto | 4–6 semanas de trabajo | Flujos acotados completos, plataforma soportada, pruebas de datos/volumen y regresión |
| Piloto y correcciones | 2–4 semanas adicionales de operación | Evidencia diaria y recuperación demostrada con el equipamiento ofrecido |
| Primera versión comercial local de alcance acotado | Aproximadamente 8–12 semanas en total, con preparación de distribución en paralelo | Candidato firmado y aprobado; funciones aplazadas excluidas expresamente |
| Madurez empresarial sostenida | Reserva inicial de 4–6 meses o más | Experiencia de campo, rendimiento, soporte y ampliación validada del alcance |

Sin hardware/cuentas o sin auditoría externa puede avanzarse con código, pero no
cerrarse esos hitos ni fijarse una fecha comercial fiable. La estimación no incluye
construir una plataforma multisucursal/alta disponibilidad ni garantiza paridad
con todas las ediciones de los productos de referencia. Facturama requiere una
estimación separada al reactivarse su desarrollo.

Reestimar al terminar el candidato local, con tiempos medidos, defectos abiertos,
matriz funcional y resultados de un operador. La calidad empresarial dependerá de
esa evidencia y del soporte sostenido, no sólo de que compile o de su aspecto visual.
