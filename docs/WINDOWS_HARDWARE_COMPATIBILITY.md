# Matriz de compatibilidad de Windows y periféricos

“Compatible” describe el alcance técnico probado; “certificado” requiere evidencia
física por fabricante, modelo, firmware y fecha.

## Windows

| Plataforma | Arquitectura | Estado Ticketfy | Alcance |
|---|---|---|---|
| Windows 11 | x64 | Objetivo soportado | Build, pruebas automáticas, SQLCipher, DPAPI, UI y paquete CI |
| Windows 10 22H2, build 19045 | x64 Intel/AMD | Laboratorio local; aceptación por equipo pendiente | Arranque y suite con datos sintéticos; instalador interno desde build 19045 |
| Windows 10/11 | x86 | No soportado por la puerta CI actual | No distribuir aunque exista script heredado de publicación |
| Windows ARM64 | ARM64/emulación | No soportado | Sin build, drivers ni pruebas de periféricos |
| Windows Server/Wine/Linux/macOS | cualquiera | No soportado | DPAPI, spooler y drivers no homologados |

Cada release comercial debe registrar edición, versión, compilación, parches, runtime,
idioma, escala de pantalla y usuario que ejecutó la prueba.

El instalador interno exige Windows x64 nativo: no anuncia soporte de ARM por
emulación ni de Windows 10 anterior a 22H2. La CI `windows-latest` corresponde a
Windows Server 2025, no demuestra aceptación en una estación Windows 11.
La compatibilidad técnica no extiende el soporte del sistema operativo:
[Microsoft limita el soporte vigente de .NET en Windows 10 a determinadas ediciones LTSC/Enterprise](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
Para una futura instalación productiva debe comprobarse además el ciclo de soporte
y las actualizaciones de la edición concreta. Las instrucciones internas recomiendan
4 GB de RAM, 1 GB libre y pantalla 1366 x 768 al 100%; son una base de laboratorio,
no una certificación de capacidad o rendimiento.

## Periféricos

| Categoría | Transporte/protocolo | Estado | Condición de certificación |
|---|---|---|---|
| Impresora 58/80 mm | Windows Spooler, ESC/POS | Compatible por protocolo | 1,000 tickets, acentos, QR, corte, papel agotado y reinicio |
| Impresora serial | COM 8N1, ESC/POS | Compatible por protocolo | baud, timeout, desconexión y cero duplicados |
| Impresora de red | TCP RAW 9100 | Compatible por protocolo | caída de red, cola, recuperación y carga |
| Cajón | RJ11/RJ12 vía impresora, `ESC p` | Compatible por protocolo | pin 2/5, una apertura por venta y control RBAC |
| Lector | USB HID, CR/LF | Compatible por protocolo | EAN/UPC/Code128, doble lectura y latencia |
| Báscula | COM ASCII, kg/g estable | Compatible por protocolo | tara, inestable, calibración y protocolo exacto |
| Mercado Pago Point | Orders API | Implementado sin certificación física | terminal real, aprobación, rechazo, timeout y conciliación |

No hay todavía modelos `ProductionCertified`; por tanto, ventas comerciales deben
declarar el hardware como pendiente de homologación y ejecutar el guion de Etapa 3.

## Detección local y avisos

Desde la base comercial de Etapa 6, el panel inferior **Periféricos** consulta el
inventario de Windows al abrir la ventana, cada 30 segundos y al pulsar Actualizar.
Es una consulta pasiva: no abre puertos, imprime ni dispara el cajón.

| Indicación | Qué demuestra | Qué no demuestra |
|---|---|---|
| Cola detectada | Windows tiene una cola con el nombre configurado | Conexión física, papel o ticket impreso |
| Cola no disponible | Windows informa un estado detenido/offline | Diagnóstico eléctrico completo |
| Puerto detectado | El puerto COM figura en el sistema | Tipo de dispositivo, protocolo o calibración |
| TCP configurada; sin comprobar | Existe un destino guardado | Conectividad o aceptación de trabajos |
| Lector pendiente de prueba | Puede operar como teclado HID + Enter | Identidad única del lector frente al teclado |
| Cajón configurado; sin sensor | Está configurado el pulso vía impresora | Presencia del cable ni apertura física |

La prueba de entrada del lector no registra ventas ni conserva el código leído en
logs. Confirmar manualmente que la entrada provino del lector. Si Windows niega la
consulta o falla WMI, el panel informa detección incompleta, no conexión confirmada.

Los resultados de impresión permanecen visibles después de cerrar el diálogo de
cobro. Un fallo no vuelve a ejecutar la venta. La configuración nueva no asume una
impresora o báscula existente ni activa por defecto el cajón. No se ha homologado
hardware real durante esta intervención: no había equipos conectados.
