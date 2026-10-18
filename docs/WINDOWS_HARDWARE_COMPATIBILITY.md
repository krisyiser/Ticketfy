# Matriz de compatibilidad de Windows y periféricos

“Compatible” describe el alcance técnico probado; “certificado” requiere evidencia
física por fabricante, modelo, firmware y fecha.

## Windows

| Plataforma | Arquitectura | Estado Ticketfy | Alcance |
|---|---|---|---|
| Windows 11 | x64 | Objetivo soportado | Build, pruebas automáticas, SQLCipher, DPAPI, UI y paquete CI |
| Windows 10 | x64 | Compatibilidad declarada; laboratorio pendiente | Requiere regresión física antes de cada release comercial |
| Windows 10/11 | x86 | No soportado por la puerta CI actual | No distribuir aunque exista script heredado de publicación |
| Windows ARM64 | ARM64/emulación | No soportado | Sin build, drivers ni pruebas de periféricos |
| Windows Server/Wine/Linux/macOS | cualquiera | No soportado | DPAPI, spooler y drivers no homologados |

Cada release comercial debe registrar edición, versión, compilación, parches, runtime,
idioma, escala de pantalla y usuario que ejecutó la prueba.

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
