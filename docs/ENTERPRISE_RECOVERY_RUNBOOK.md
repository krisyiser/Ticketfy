# Recuperación integral, cobros y reembolsos interrumpidos

Procedimiento del candidato local, actualizado el 27 de octubre de 2026. Utilizar primero datos de laboratorio. No autoriza una migración de la base operativa ni habilita Facturama.

## Dos tipos de respaldo, dos alcances

Los respaldos automáticos `.db` protegen la base comercial y permiten restauración dentro de la instalación que conserva sus llaves. No bastan por sí solos si se pierde el perfil Windows o el equipo.

El botón **Captura documental** de Inventario sólo guarda una valoración histórica dentro de esa misma base. No protege contra pérdida del disco, no restaura el sistema y no acredita un conteo físico. Su historial requiere permiso de costos; no lo use como sustituto de ninguno de los respaldos siguientes.

El paquete `.tfyrecovery` reúne una copia consistente de la base comercial, una copia de la auditoría y las llaves necesarias para abrirlas y verificar su integridad. Todo el contenido se cifra y autentica con una frase de recuperación que el programa no conserva. La preparación exige permiso de seguridad y todos los turnos cerrados. Las dos bases se capturan por separado: no es una transacción distribuida entre archivos.

No incluye credenciales bancarias, licencia ni documentos fiscales externos. Las integraciones deben configurarse de nuevo; Facturama permanece desactivado. El formato actual admite hasta 256 MiB de contenido comprimido y 512 MiB descomprimido. Superar ese límite bloquea la operación con un mensaje; no se omiten tablas para producir una copia incompleta.

## Crear y comprobar el paquete

1. Resolver cobros y reembolsos pendientes y cerrar todos los turnos.
2. Iniciar sesión con un usuario autorizado. Abrir **Configuración → Sistema y Seguridad → Recuperación ante pérdida del equipo**.
3. Elegir un archivo nuevo, escribir y repetir una frase de al menos 20 caracteres. Guardarla en custodia separada del equipo y del paquete. Si se pierde, Ticketfy no puede recuperarla.
4. Confirmar la custodia y crear el paquete. Copiarlo a otro dispositivo o almacenamiento autorizado. Guardarlo sólo en el disco del POS no protege frente a pérdida del equipo.
5. Seleccionar el paquete e introducir su frase para **Verificar paquete**. El programa valida autenticación, inventario de archivos, hashes, integridad SQLite, esquema y cadena de auditoría en archivos temporales aislados.
6. Registrar fecha, responsable, ubicación de custodia y resultado. Nunca escribir la frase en un ticket de soporte, log o documento compartido.

Las copias temporales sin cifrar de la auditoría permanecen en una carpeta local con permisos Windows restringidos al usuario. La salida puede estar en otro dispositivo; sólo recibe el archivo cifrado. Los snapshots conservan el modo de cifrado compatible con la [SQLite Backup API de SQLCipher](https://discuss.zetetic.net/t/using-the-sqlite-online-backup-api/2631).

## Recuperar sin sobrescribir

1. Conservar intacta la instalación dañada. No borrar bases, llaves, archivos WAL ni auditoría.
2. Cerrar Ticketfy. Iniciar `Ticketfy.Desktop.exe --recovery`. Si el arranque se detuvo por un fallo de base o servicios, usar el botón **Abrir asistente de recuperación integral** en la pantalla de error.
3. Seleccionar el paquete, introducir la frase y elegir una ruta absoluta a una carpeta **que todavía no exista**, dentro de una carpeta existente. No seleccionar la instalación actual.
4. Confirmar el destino. El asistente verifica antes de publicar; una contraseña incorrecta, archivo alterado, esquema desconocido o auditoría inválida bloquean la restauración. Las llaves se protegen con DPAPI para el usuario Windows de destino.
5. La nueva carpeta contiene `Database`, `Secrets` y un comprobante `recovery-receipt.json`, sin secretos. La ruta de respaldos de la copia se cambia a su propia carpeta `Backups`, para no escribir en la instalación de origen. El comprobante distingue el hash de origen del de la copia configurada.
6. Para abrir esa copia, iniciar `Ticketfy.Desktop.exe --data-directory "C:\ruta\a\la\copia"`. Este argumento no cambia la ubicación de la instalación anterior. No poner una base SQLite compartida por red al alcance de varias terminales.
7. Iniciar sesión con contraseña antes de probar un PIN heredado. El acceso rápido sólo dura 15 minutos desde esa verificación en la misma ejecución; cerrar sesión o reiniciar exige verificar nuevamente la contraseña. Las aprobaciones sensibles no admiten PIN. Si se solicita actualizar una contraseña débil, comprobar la anterior, registrar y confirmar una nueva, y volver a iniciar sesión. El cambio asistido deshabilita el PIN anterior; un administrador puede registrar uno nuevo. Si se pierde la confirmación del cambio, probar la nueva contraseña antes de repetirlo. Reiniciar no elimina el bloqueo temporal por intentos fallidos.
8. Validar la licencia, zona comercial, caja, usuarios y configuración. Conciliar ventas, saldos por documento, existencias, turnos y auditoría contra la fecha del paquete antes de autorizar operación.

La prueba automatizada demuestra recuperación con llaves sintéticas sin archivos DPAPI del perfil de origen y nueva protección DPAPI en el usuario de laboratorio. **No equivale todavía a un ensayo en otro perfil Windows o equipo físico.** Ese ensayo y la revisión independiente del formato criptográfico siguen siendo puertas de aprobación.

## Frecuencia, pérdida tolerable y tiempo de recuperación

Hasta contar con una programación integral aprobada, crear manualmente un paquete después del cierre diario y antes de una actualización. Los respaldos automáticos de base continúan según su política, pero no deben confundirse con un paquete integral actualizado.

Objetivos propuestos, todavía sujetos a aceptación: pérdida máxima de una jornada para una pérdida total del equipo (RPO de hasta 24 horas si se cumple la copia diaria) y recuperación dentro de cuatro horas (RTO a medir en otro equipo, incluyendo licencia y conciliación). Si no se crea o custodia la copia diaria, esos objetivos no se cumplen. Una prueba de pocos segundos en memoria no acredita el RTO de la operación.

## Auditoría incompatible, ambigua o con firmas ausentes

Las entradas nuevas utilizan la versión 2 de la codificación de integridad. La verificación conserva las firmas de versión 1 compatibles; no las reemplaza. Una versión desconocida o un registro antiguo que contenga el separador de campos del formato original se rechaza para revisión. El mismo control se aplica a los paquetes de recuperación: tener la frase correcta no autoriza a ignorar una auditoría inválida.

Si aparece un fallo de integridad, detener la puesta en servicio y conservar bases, llaves y respaldo anterior a la actualización. No borrar hashes, quitar protecciones ni regenerar firmas para hacer pasar la verificación. Registrar la versión del ejecutable, el mensaje y el momento del fallo; escalar a soporte sin enviar secretos ni bases por canales ordinarios.

Sólo la transición conocida desde el esquema antiguo sin firmas autoriza su incorporación inicial. Esta operación establece una línea de base desde ese momento; no demuestra que los datos anteriores nunca fueran alterados. La incorporación trabaja por lotes dentro de una transacción y revierte todos los lotes ante fallo o cancelación. Una cadena que mezcla entradas firmadas y sin firma no se incorpora automáticamente.

La migración del esquema y esa incorporación son pasos distintos. Si se interrumpe después de migrar pero antes de confirmar la incorporación, el siguiente arranque se detiene: no vuelve a firmar automáticamente. Recuperar en un destino nuevo desde un paquete verificado compatible, o solicitar una revisión técnica de la copia preservada. No autorizar operación sobre un historial cuya integridad no pudo comprobarse.

## Avisos de mantenimiento durante la jornada

Abrir el panel **Mantenimiento** en la parte superior de la ventana. Sus cuatro estados corresponden a auditoría, respaldos automáticos, alertas de existencias y recomendaciones. «Pendiente» o «Sin ejecución exitosa confirmada en esta sesión» no significa que no exista ningún respaldo anterior; significa que esta ejecución no ha confirmado uno. Revisar también la carpeta y el diagnóstico antes de decidir qué copia utilizar.

Si falló un respaldo, atender espacio libre, permisos y disponibilidad de la unidad. El fallo no bloquea las ventas y el trabajador vuelve a intentarlo en el siguiente intervalo. Una ejecución omitida porque aún no toca no borra la advertencia anterior. Si los respaldos o las pruebas de restauración aparecen desactivados, solicitar al responsable revisar la configuración; no asumir protección automática.

Si aparece **Operación protegida: escrituras bloqueadas**:

1. Detener nuevos cobros y movimientos físicos. Si ya se entregó efectivo o se envió una orden al banco, conservar la referencia original: el aviso no demuestra rechazo ni cancelación.
2. Abrir el detalle y usar **Exportar diagnóstico seguro**. Contiene estados y fechas, no bases ni credenciales. Si el disco no permite exportar, anotar el mensaje y la versión sin copiar secretos.
3. Usar **Volver a verificar auditoría** para una comprobación de sólo lectura. El botón no repara ni vuelve a firmar el historial. El fallo anterior continúa vigente mientras se verifica.
4. Si la verificación tiene éxito, consultar los documentos e intentos originales con un usuario autorizado antes de repetir cualquier acción. Si sigue fallando, conservar archivos y llaves y solicitar soporte; no quitar firmas ni protecciones.

El bloqueo también aparece si no hay una comprobación exitosa en esta ejecución, si tiene más de 26 horas, si se detiene la verificación o si el reloj queda antes del último éxito. No se anulan retrospectivamente operaciones que ya superaron el último control de confirmación. Una orden bancaria pendiente o completada sin venta confirmada requiere conciliación, no un nuevo cobro.

Las acciones de diagnóstico y verificación no requieren permisos comerciales y no los conceden. Las consultas de negocio siguen exigiendo autorización. Si la auditoría ya impide el arranque, usar el procedimiento de recuperación anterior: este panel no sustituye los controles de inicio.

## Cobro pendiente o respuesta perdida

**Detener espera** no cancela el cobro bancario. **Cerrar** no revierte una operación ya enviada. No entregar dinero de nuevo, cambiar de medio ni crear otra referencia para resolver un mensaje de error.

1. Conservar la referencia local visible, importe, desglose por medio, caja y turno. No copiar números completos de tarjeta ni credenciales.
2. Si la espera tarda, usar **Detener espera** sólo para interrumpir la espera local. La terminal o el banco pueden completar la operación después. Mientras termina esa llamada, el diálogo impide iniciar otro cobro.
3. Consultar el historial con un usuario autorizado. Si aparece la venta original o el mensaje **Venta registrada**, no cobrar otra vez. Un fallo de impresión o de actualización de pantalla no anula el registro; reimprimir desde el historial cuando corresponda.
4. Si queda pendiente, mantener abierto el turno y revisar **Control comercial → Recuperar cobros** desde la caja original con permiso de recuperación. El sistema bloquea un segundo cobro en ese turno y su cierre hasta resolver la intención; no bloquea por esa causa otras cajas lógicas.
5. Si hay tarjeta, usar **Consultar banco sin volver a cobrar**. Requiere permisos de recuperación y conciliación bancaria en la caja original. Consulta la orden guardada; si se perdió su ID durante la creación, obtener el ID de esa orden original del proveedor e introducirlo en el campo indicado. No usar el folio local ni crear otra orden. Una orden de otra referencia, terminal, pago o importe se rechaza.
6. Para completar, comprobar los fondos de cada medio. Tarjeta requiere evidencia bancaria validada y persistida por el importe exacto; una etiqueta histórica «completado» no basta. Transferencia requiere su confirmación registrada. El efectivo de un cobro mixto también debe verificarse. La consulta bancaria no registra automáticamente la venta ni marca la casilla del supervisor.
7. Para descartar, comprobar que no se retuvieron fondos e indicar el motivo. Una evidencia bancaria confirmada o incierta impide descartar. Una etiqueta antigua «fallido» o «cancelado» tampoco demuestra ausencia de fondos: consultar la orden original. La decisión conserva al operador original e identifica al supervisor; la intención descartada no se reutiliza para vender.

La lista muestra 100 cabeceras por página, ordenadas por referencia local, no por fecha. El número de pendientes corresponde a toda la búsqueda de esa caja. Use **Anteriores**, **Siguientes** y **Buscar pendientes / actualizar**; el filtro de catálogos de la parte superior no filtra estos cobros. Cambiar la búsqueda limpia la selección y la comprobación de fondos. Cada página consulta el estado actual: actualizar desde el inicio incorpora también referencias nuevas anteriores a la posición recorrida.

**Consulta no disponible** no significa que no haya pendientes. Si la resolución ya se registró y falla sólo la actualización de la lista, no vuelva a cobrar: consulte de nuevo. En una ventana pequeña, desplácese dentro de **Recuperar cobros** para alcanzar la consulta bancaria y el botón de resolución.

La consulta envía sólo una lectura al proveedor; guarda localmente su evidencia y diario. Un fallo de red o respuesta ilegible no borra una captura anterior. Si el banco informa revisión, confirmación pendiente en terminal, devolución parcial/total, estado desconocido o evidencia contradictoria, conservar el pendiente y escalar con su referencia y comprobantes autorizados. No editar la base, borrar el intento ni llamar de nuevo al cobro para forzar una respuesta. La confirmación manual de una transferencia no acredita un cargo de tarjeta.

La [consulta Point por ID](https://www.mercadopago.com.mx/developers/en/reference/in-person-payments/point/orders/get-order/get) admite órdenes de menos de tres meses. Si no puede obtenerse la orden o el proveedor requiere comprobar la terminal, el flujo automático no permite resolver por una casilla o un comprobante sin validación. Escalar a soporte; no anunciar una cancelación por haber detenido la espera. El acuse de una petición de cancelación tampoco equivale a su confirmación final.

Después de reiniciar, no asumir que todo se resolvió. Los estados `RequiresReconciliation` permanecen para intervención; los históricos marcados `Failed` con evidencia bancaria contradictoria vuelven a mostrarse. Un registro de efectivo antiguo sin evidencia no permite inferir qué ocurrió físicamente. Un cambio de composición de combo o datos recuperables inválidos exige revisión técnica, no otro cobro.

El arranque revisa los candidatos automáticos por lotes y relee cada intención antes de cambiarla. Una venta ya confirmada no vuelve a pendiente por esa recuperación. Un documento ilegible, incompleto o con partidas/pagos nulos queda para revisión sin impedir la recuperación de los demás. Si la base no permite guardar ese estado incierto, se detiene el arranque: no se omite el fallo de almacenamiento.

Si aparece un aviso de tamaño o datos no verificables en un cobro histórico, conservar la referencia y solicitar revisión técnica. El lector no carga un JSON de intención que exceda 2 MiB; tampoco lo recorta ni lo reemplaza en la base. No se puede interpretar esa advertencia como ausencia de fondos. No edite el JSON, no borre la intención y no fragmente un cobro ya enviado para crear referencias nuevas. Una intención con datos inválidos puede impedir también la consulta automática del banco; el supervisor debe conservar el pendiente y obtener la evidencia por un canal autorizado.

`PreserveTerminalEvidence` conserva fondos conocidos y añade prueba explícita de ausencia de fondos, fecha y huella de la respuesta. No inventa fechas ni confirma rechazos antiguos. Una vez guardada evidencia, no se permite bajar el esquema borrándola. Para contingencia, conservar un respaldo integral verificado y aplicar el procedimiento de restauración aislada; no forzar un downgrade sobre la base actual.

## Documento nuevo que excede los límites

Una venta o compra nueva admite hasta 1.000 partidas. La venta admite además hasta 10.000 movimientos físicos al expandir sus combos. Los campos y el texto conjunto tienen presupuestos propios; un documento con pocos productos también puede excederlos. El mensaje indica si debe corregirse un campo o dividirse la operación.

Si todavía no hay intención de cobro ni movimiento externo, retirar partidas del borrador o dividir la operación antes de cobrar o recibir mercancía. El carrito permite aumentar una partida existente si hay stock; la compra permite retirar y volver a añadir. Corregir un texto rechazado no autoriza a cambiar un comprobante real.

Si ya se cobró, se entregó efectivo, se recibió mercancía o hay una referencia pendiente, detener nuevos movimientos y seguir la recuperación correspondiente. No generar otra operación para eludir el límite ni repetir un pago por un error de lectura. Estos presupuestos no acreditan rendimiento en una caja instalada ni sustituyen la conciliación.

## Compra histórica con detalle pendiente o no verificable

En el historial de compras, **Total registrado** es el importe de la cabecera original. No acredita que sus partidas sean correctas ni representa el saldo por pagar. Consulte el estado de cuenta conciliado del proveedor para conocer la deuda; no interprete una lista de partidas vacía como deuda cero.

1. Si el estado indica **pendiente de consulta individual**, abra **Ver detalle**. En Control comercial, use **Consultar partidas de la compra seleccionada** antes de elegir una devolución. Este estado puede deberse al volumen conjunto de la página, no a un documento incorrecto.
2. Si indica **Desglose no verificable**, conserve la referencia y solicite revisión técnica con el comprobante original. La aplicación no imprime ni autoriza devolución de mercancía con ese desglose. No cree una compra duplicada, altere partidas ni cambie el total para eludir el control.
3. Si una devolución anterior no coincide en cantidad o importe con la compra, detenga nuevos retornos y concilie la evidencia. El rechazo no crea otro crédito ni descuenta inventario.
4. Un pago a proveedor usa el saldo conciliado de su ledger, no el total de la cabecera. Si falta confirmación de un pago ya enviado, consulte y resuelva el mismo intento; no vuelva a entregar dinero sólo porque falle la lectura del documento.
5. Al cambiar búsqueda o cerrar sesión, vuelva a consultar. Las respuestas pendientes se descartan para no mostrar o imprimir datos de la sesión anterior.

Los controles de lectura conservan los originales en la base. Los documentos aceptados se leen completos: máximo 1.000 partidas y 512 KiB de texto por documento. Una página puede diferir detalles cuando alcanza 10.000 partidas u 8 MiB de texto; sus cabeceras siguen visibles. Estos límites no son una certificación de capacidad. **Enviado al controlador** tampoco acredita que salió papel: compruebe la impresión física cuando exista hardware.

## Transferencias para abonos y recargas

En **Control comercial → Transferencias**, Ticketfy registra depósitos ya comprobados; no solicita ni envía dinero al banco. El comprobante del cliente por sí solo no demuestra recepción en la cuenta del comercio.

1. Preparar el intento con destino, importe y concepto antes de aplicarlo. Conservar la referencia local que muestra la confirmación. Preparar no reduce deuda ni incrementa el monedero.
2. Usar **Consultar pendientes** y buscar por referencia local o identificador del destino. Las flechas recorren páginas de 100; el total corresponde a toda la búsqueda, no sólo a la página. Se ordena por referencia, no por fecha. Al cambiar búsqueda, página o modalidad se retiran la selección y las casillas de comprobación.
3. Comprobar importe, destinatario y recepción efectiva; indicar la referencia bancaria única y seleccionar **Confirmar y aplicar / recuperar sin volver a cobrar**. La confirmación se conserva antes de aplicar el abono o recarga. Si falla esa aplicación, consultar y resolver el mismo intento; no registrar otro depósito ni pedir otro pago.
4. Si el cajero original ya no está disponible, un supervisor con permisos de conciliación y recuperación puede resolver desde la **caja original**, sin abrir otro turno ni usar las credenciales ajenas. Para aplicar también necesita permiso de abonos de caja o de monedero, según el destino. El turno original debe seguir abierto y correctamente atribuido. El movimiento conserva cajero, caja y turno originales; el diario identifica al aprobador real.
5. Si se comprobó que no entró dinero, descartar con motivo y declaración de ausencia de fondos. Una confirmación bancaria existente impide ese descarte.
6. Si entró dinero pero no puede aplicarse —por ejemplo, la deuda ya se liquidó por otro medio—, documentar la devolución bancaria sólo después de verificar que ocurrió. Indicar su referencia real y motivo. Este registro no ejecuta la devolución ni crea una salida de efectivo local.
7. Consultar **Historial de depósitos aplicados / devueltos** para recorrer todos los comprobantes con permiso de reportes. Incluye todas las cajas, conserva sus atribuciones y permite buscar por referencia local, documento o referencia bancaria. No son ventas adicionales. Las páginas usan lecturas nuevas; actualizar vuelve al inicio para incluir registros anteriores a la posición ya recorrida.

**Resolución registrada** seguida de **No se pudo actualizar la lista** significa que falló la consulta posterior, no que deba repetirse el movimiento. Conservar la referencia y volver a consultar. Una consulta no disponible tampoco significa que no existan pendientes.

Si aparece un comprobante incompatible, un descarte contradictorio o un movimiento previo sin comprobante coherente, detener esa resolución y conservar la evidencia para soporte. El pendiente sigue bloqueando el cierre; no se fabrica otro abono o recarga para ocultar la inconsistencia. No editar ni borrar el historial. Estos casos requieren revisión de la copia preservada y comprobantes originales.

## Reembolsos de saldos a favor

En **Control comercial → Saldos a favor**, distinguir una aplicación a otra deuda —no mueve dinero— de un reembolso de cliente —sale dinero— o de proveedor —entra dinero—.

1. Consultar documentos de la cuenta, seleccionar origen, importe, medio y motivo. Origen y destino tienen páginas independientes de 100; los totales abarcan toda la cuenta. Cambiar selección elimina la confirmación anterior. Un fallo de consulta o una reserva incompatible no significa saldo cero ni autoriza preparar otro reembolso para compensarlo.
2. Guardar la solicitud **antes** de entregar o recibir dinero. Reserva el saldo sin modificar todavía la caja ni liquidar la cuenta. No puede utilizarse ese saldo en otra operación y el turno no puede cerrarse mientras siga pendiente.
3. Ejecutar el movimiento físico o bancario autorizado fuera de este registro. Ticketfy no inicia transferencias ni devoluciones bancarias desde esta pantalla.
4. Seleccionar la solicitud persistida, revisar evidencia y confirmar el movimiento. Para tarjeta o transferencia, indicar la referencia única real. Se publican liquidación, cuenta, caja cuando corresponde y diario en una sola transacción.
5. Si se interrumpe la aplicación, volver a consultar solicitudes pendientes. **No repetir automáticamente la entrega ni el envío bancario.** Si ya se movieron fondos, confirmar la solicitud existente. Si se comprobó que no hubo movimiento, descartarla con motivo y liberar el saldo.
6. Otro gerente autorizado puede resolver desde la caja original, incluso si el operador está inactivo. El documento conserva al operador y turno originales y la resolución identifica al aprobador.

Un rechazo bancario, una referencia repetida o un fallo de DB pueden dejar la solicitud pendiente, pero no publican parcialmente sus asientos. Revisar la evidencia; no crear otro intento para ocultar el pendiente.

La consulta tiene búsqueda y páginas de 100 solicitudes de la caja actual, ordenadas por referencia local. El total abarca toda la búsqueda; use **Solicitudes anteriores** y **Solicitudes siguientes** para recorrerla. Cambiar búsqueda, página o selección elimina la referencia bancaria y las confirmaciones anteriores. Actualizar desde el inicio incorpora también solicitudes nuevas que queden antes de la posición recorrida.

**INCONSISTENCIA: revisión técnica** indica que existe una resolución o movimiento previo que no respalda de forma coherente la solicitud. El registro continúa pendiente y bloquea el cierre del turno; la pantalla no permite confirmarlo ni descartarlo como una solicitud normal. Conservar los comprobantes y solicitar revisión de una copia preservada. No borrar la resolución ni crear otro movimiento para desbloquear el cierre.

Si aparece **Solicitud guardada** o **Resolución registrada** y después falla la actualización, no repetir la operación. Consultar la referencia indicada. **Consulta no disponible** no acredita ausencia de pendientes ni un saldo cero.

## Reembolsos bancarios de devoluciones de ventas

En **Control comercial → Conciliación**, la lista de reembolsos bancarios incluye todas las cajas, con caja, turno y operador de origen. Requiere permiso de conciliación bancaria; registrar una conciliación nueva requiere además un turno propio abierto. El reembolso conserva su atribución original y la confirmación registra al operador, caja y turno que lo verificaron.

1. Buscar el reembolso o la devolución, o filtrar por caja, turno u operador. Recorrer las páginas con **Reembolsos anteriores** y **Reembolsos siguientes**; el total corresponde a toda la búsqueda.
2. Seleccionar el documento y comprobar el importe completo contra la evidencia bancaria. La referencia del cargo original o de otro depósito no acredita un reembolso.
3. Introducir una referencia bancaria no utilizada, marcar la comprobación y elegir **Conciliar reembolso externo**. Esta acción registra la verificación; no ordena devolver dinero al banco ni genera una salida de efectivo.
4. Si se pierde la respuesta, volver a consultar el mismo reembolso. Una confirmación ya registrada no debe ejecutarse físicamente de nuevo. Un error al refrescar después de guardar conserva el aviso de conciliación registrada.
5. Si la conciliación histórica difiere en importe o carece de atribución o referencia, el pendiente muestra **INCONSISTENCIA**. No confirmar otro comprobante sobre el anterior ni editarlo: preservar la evidencia y escalar a soporte.

El cierre de caja no sustituye esta conciliación bancaria. La conciliación diaria continúa señalando los reembolsos externos pendientes aunque su turno ya esté cerrado. Las referencias nuevas de depósitos, reembolsos y pagos bancarios a proveedores se contrastan con las operaciones bancarias registradas; no se reutiliza un comprobante para justificar dos movimientos.

## Venta o devolución con evidencia inconsistente

Si una devolución o cancelación indica que no hay evidencia coherente, conservar el folio y solicitar conciliación técnica. No entregar dinero ni mover mercancía para intentar resolver el aviso. El control compara el original con sus partidas, pagos y movimientos de inventario, y verifica los acumulados de devoluciones anteriores; un registro faltante, contradictorio o demasiado grande no se corrige creando otra operación.

- **Cancelación registrada** confirma la compensación local, aunque después aparezca **No se pudo consultar el historial**. Volver a consultar; no repetir la entrega física por el fallo de lectura.
- **No se registró una nueva cancelación** no autoriza otra entrega: consultar si el folio ya fue cancelado o devuelto.
- **No se pudo confirmar la cancelación** requiere revisar el mismo folio antes de actuar. No cambiar la identidad de la operación para forzar un segundo intento.
- Tarjeta y transferencia siguen el procedimiento de conciliación bancaria anterior. Cancelar una venta no ejecuta un reembolso externo.

La revisión de soporte se realiza sobre una copia preservada y con autorización. No borrar partidas, reembolsos o movimientos ni modificar saldos originales para eludir el bloqueo.

## Otros avisos y límites

- En compras, **Estado financiero no verificable** impide un nuevo pago o devolución con evidencia contradictoria. Conservar el documento y revisar sus pagos, partidas, inventario y diario sobre una copia autorizada. **Pagos documentados** no reconstruye los pagos perdidos de una compra histórica; su saldo sólo es utilizable después de la conciliación explícita.
- **Consultar movimientos** recorre el historial financiero de la compra; sus saldos incluyen todas las páginas. El crédito aplicado y el reembolso recibido no se suman como otro pago al proveedor. Una reserva de reembolso reduce el crédito disponible sin confirmar todavía movimiento de fondos.
- Si después de pagar o devolver aparece **Operación confirmada. No se pudo actualizar el detalle**, volver a consultarlo sin repetir el pago ni la entrega de mercancía.
- `READ_BUDGET` durante un cierre conserva el turno abierto. Solicitar revisión técnica de una copia; no eliminar movimientos, dividir registros confirmados ni modificar saldos para evitar el límite. Volver a pulsar Cerrar no permite omitir la evidencia.

- **Desglose pendiente de consulta individual** en Historial requiere **Ver detalle**: se alcanzó el presupuesto de página, no desaparecieron partidas. **Desglose no verificable** conserva el folio, pero impide imprimir ese detalle como ticket válido o usarlo para devolver. Solicitar revisión de una copia preservada; no editar el original ni volver a cobrar.
- La conciliación diaria recorre **Turnos anteriores** y **Turnos siguientes**. Sus sumas son de la página indicada: revisar todas. **No calculado** o `READ_BUDGET` no significa caja vacía ni turno cuadrado; la evidencia excede el presupuesto o contiene texto no verificable. Conservar el turno y escalar a soporte. Sin conteo final, contado y diferencia permanecen desconocidos.
- **CONCILIADO INTERNO** acredita sólo comprobaciones internas del turno, no banco, inventario y estados de cuenta. Las fechas ilegibles permanecen como incidencias sin asignarse automáticamente al día.
- Un fallo de impresión, asistencia o respaldo posterior a confirmar una apertura/cierre no revierte el dinero. Atender el aviso y consultar el historial; no repetir la operación monetaria.
- Las notas históricas sin caja se asignan mediante revisión de supervisor. El autor desconocido no se convierte en `admin` ni en el usuario que revisa.
- Los diagnósticos exportados omiten mensajes libres y nombres de archivos de logs: contienen información ambiental y resúmenes de fechas/niveles. No enviar bases, paquetes integrales ni llaves como diagnóstico ordinario.
- La versión sigue pendiente de instalación/actualización en VM, firma, ensayo de otro perfil/equipo, hardware real, revisión de seguridad independiente y piloto conciliado.
