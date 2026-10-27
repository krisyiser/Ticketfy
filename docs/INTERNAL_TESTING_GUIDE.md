# Pruebas internas de Ticketfy

Este laboratorio permite probar los flujos locales con datos inventados, sin utilizar la base operativa. El candidato es un paquete Windows x64 sin firma; su aceptación comercial requiere las verificaciones externas indicadas en [el avance empresarial](ENTERPRISE_REMEDIATION_PROGRESS.md). Facturama y la infraestructura aplazada permanecen desactivados.

## Laboratorio local preparado el 27 de octubre de 2026

El gate local `quality-9246d5e150944e698dbf3ec37842a241` aprobó 1.248 pruebas, arranque empaquetado, persistencia y restauración. Los dos ZIP coinciden en SHA256 `C9C0E01EC141243D8803DEEAFA6ED48815CF82FC6547EDA4FC4F71A616C8B908`. La aceptación manual está pendiente. La carpeta de datos del siguiente laboratorio está vacía y lista para iniciar la aceptación:

```powershell
./artifacts/internal-labs/1cc0412e03ac4e1aa25d4d801ed1a3e2/Start-Laboratory.ps1
```

Ejecutar desde la raíz del repositorio. El lanzador comprueba la identidad del paquete y selecciona su propia carpeta de datos. Este laboratorio corresponde a la evidencia local anterior a consolidar el commit; otros candidatos requieren un laboratorio nuevo, preparado como se describe abajo. La jornada manual todavía debe completarse y registrarse; no se afirma homologación de hardware ni aprobación comercial.

## Preparar una instalación aislada

Desde PowerShell, en la raíz del repositorio, ejecutar:

```powershell
./eng/new-internal-lab.ps1 -PackageDirectory './artifacts/<gate-aprobado>/package-a/publish-win-x64'
```

Sustituir `<gate-aprobado>` por la ejecución integral identificada en [la evidencia vigente](ENTERPRISE_REMEDIATION_PROGRESS.md#evidencia-automatizada). El script exige la comprobación de arranque de ese ejecutable y prepara una carpeta nueva en `artifacts/internal-labs`, un registro del paquete y `Start-Laboratory.ps1`. No inicia Ticketfy ni copia bases o credenciales. Cada ejecución prepara un laboratorio distinto.

Ejecutar el `Start-Laboratory.ps1` devuelto para abrir el programa. Utiliza siempre `--data-directory` con la subcarpeta `Data` del laboratorio. Volver a ejecutar ese mismo lanzador conserva los datos de la prueba. No iniciar directamente el ejecutable: sin ese argumento utiliza la ubicación habitual del producto. No mover el lanzador ni redirigir sus carpetas.

La primera apertura debe llegar al asistente de configuración. Crear una empresa ficticia y un administrador exclusivo del laboratorio, con contraseña propia de al menos 15 caracteres. No reutilizar contraseñas personales. La evaluación local tiene su política normal; no se genera una licencia falsa ni se modifica el reloj. Mantener los datos bancarios y fiscales sin configurar y guardar los respaldos dentro del laboratorio.

Cerrar y abrir con el mismo lanzador para comprobar la persistencia. Registrar el paquete, la fecha y el responsable de la prueba. El programa no debe abrir la información de la instalación operativa.

En una instalación nueva, probar además la interrupción del asistente: cerrar después de crear el administrador y volver a abrir el mismo laboratorio. Debe pedir autenticación y retomar los datos del negocio. Repetir después de guardarlos: debe retomar usuarios adicionales. Finalizar, volver a entrar y comprobar que aparece el POS. Los errores de guardado deben conservar el paso pendiente; no crear otro administrador ni borrar la carpeta para resolverlos.

## Jornada comercial de referencia

Usar un producto físico por pieza, sin promoción activa, precio final $50, costo $30 y existencia inicial cero. Crear un cliente sin deuda y un proveedor nuevos. Todos los importes de esta tabla son datos ficticios en MXN. Comprobar el total mostrado antes de confirmar cada documento.

| Paso | Operación | Efectivo esperado | Existencia | Deuda al proveedor | Deuda del cliente |
|---|---|---:|---:|---:|---:|
| 1 | Abrir turno con $1.000 | $1.000 | 0 | $0 | $0 |
| 2 | Recibir 10 piezas a $30; pagar $100 en efectivo | $900 | 10 | $200 | $0 |
| 3 | Vender 2 piezas por $100 en efectivo | $1.000 | 8 | $200 | $0 |
| 4 | Devolver 1 pieza de esa venta; reintegrar $50 | $950 | 9 | $200 | $0 |
| 5 | Devolver 2 piezas al proveedor por $60 a crédito | $950 | 7 | $140 | $0 |
| 6 | Pagar los $140 restantes al proveedor en efectivo | $810 | 7 | $0 | $0 |
| 7 | Registrar gasto de $10 en efectivo | $800 | 7 | $0 | $0 |
| 8 | Cancelar ese gasto y registrar su restitución | $810 | 7 | $0 | $0 |
| 9 | Vender 1 pieza al cliente por $50 a crédito | $810 | 6 | $0 | $50 |
| 10 | Registrar abono del cliente de $20 en efectivo | $830 | 6 | $0 | $30 |
| 11 | Cerrar con conteo de $830 | $830 | 6 | $0 | $30 |

Revisar después de cada paso el documento, Kardex, estado de cuenta y movimientos de caja. Deben conservar el operador, caja, turno, importe y referencia de la operación. En compras, «Consultar movimientos» muestra pagos, devoluciones y aplicaciones de crédito por separado; los totales incluyen todas sus páginas. La devolución al proveedor genera crédito, no recepción de efectivo.

Al cerrar, el conteo por denominaciones debe sumar $830 y la conciliación interna no debe presentar diferencias ni pendientes. Reiniciar y consultar los mismos documentos: sus folios, saldos y cantidades deben permanecer. Un mensaje de fallo después de confirmar una operación obliga a consultar su historial antes de repetir un movimiento físico.

## Comprobaciones adicionales

| Flujo | Comprobación de aceptación |
|---|---|
| Producto y promoción | Rechazar códigos duplicados y cantidades inválidas; aplicar una promoción al producto previsto, comprobar vigencia y desactivación. Verificar que «Antes de promociones» menos «Promociones» sea igual al total. Hacerlo en una jornada separada de la tabla. |
| Preparación de cobro | Pulsar Cobrar dos veces; debe abrir un solo diálogo. Vaciar o cambiar el ticket, cambiar cliente o bloquear/salir durante una comprobación no debe abrir un cobro desde la respuesta anterior. Si aparece un fallo de promociones, revisar el ticket y reintentar; no asumir que el importe anterior ya fue confirmado. |
| Cantidades fraccionarias | Usar otro producto con unidad fraccionaria; recibir y devolver milésimas y comprobar tres decimales en detalle y ticket. |
| Crédito de proveedor | Pagar una compra y devolver mercancía; comprobar saldo negativo. Aplicar parte a otra compra y reservar un reembolso; no contabilizar dos veces la reserva. |
| Históricos | «No calculado» y «no verificable» requieren revisión; nunca tratarlos como cero. La conciliación histórica documenta un saldo comprobado, no reconstruye pagos perdidos. No alterar una base a mano para probar daños: esos casos están en las regresiones sintéticas. |
| Permisos | Entrar con cuentas de laboratorio de cajero y supervisor. Comprobar las acciones permitidas, la ocultación de costos y que salir de sesión elimina detalles anteriores. |
| Contraseñas | El alta y cambio deben rechazar valores comunes y aceptar una frase propia que cumpla el mínimo. Si una cuenta de laboratorio anterior requiere reemplazo, comprobar la contraseña anterior, cambiarla y volver a iniciar sesión; el PIN anterior debe quedar deshabilitado. No editar la base para crear ese caso: la suite incluye la transición sintética. |
| Configuración | Entrar a Empresa, recorrer sus pestañas, guardar datos ficticios, salir y volver a entrar. Comprobar que se conservan y que vaciar un contacto opcional no recupera el anterior. Actualizar una sección exige su permiso; un fallo debe ocultar los datos e impedir guardar. «Volver al POS» debe regresar mediante navegación autorizada. |
| Fiscal pausado | Confirmar el aviso de facturación deshabilitada y el estado «NO VERIFICADO». Los certificados y la contraseña no deben estar disponibles para edición. No introducir credenciales fiscales. |
| Impresión sin equipo | Solicitar ticket y reimpresión; comprobar vista previa o mensaje de falta/fallo de impresora. «Enviado al controlador» no demuestra salida de papel. |
| Respaldo | Crear y verificar una copia; probar restauración sólo en otro directorio nuevo siguiendo el [procedimiento de recuperación](ENTERPRISE_RECOVERY_RUNBOOK.md). |
| Diagnóstico | Exportar el diagnóstico del laboratorio y comprobar que no contiene contraseñas, llaves ni notas personales. |

Tarjetas, terminales, cajones, básculas e impresión física requieren hardware y cuentas de prueba autorizadas. La suite automatizada utiliza dobles de prueba para esos contratos; no ingresar referencias ficticias en un servicio bancario real ni configurar Facturama.

## Registrar incidencias

Guardar versión, hash del paquete, paso, resultado esperado, resultado observado y referencia del documento. Adjuntar sólo el diagnóstico sanitizado. No incluir contraseñas, llaves ni una base operativa. Para un fallo monetario, conservar el laboratorio y consultar el mismo documento; no borrar registros ni compensar diferencias con otra venta o pago.

La aceptación interna requiere completar esta jornada sin pérdida ni duplicación de registros, reiniciar conservando resultados y verificar respaldo/restauración. El gate automatizado respalda el candidato; la aceptación por una persona y la homologación externa son comprobaciones distintas.
