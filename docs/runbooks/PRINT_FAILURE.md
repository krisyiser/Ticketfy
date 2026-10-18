# Runbook — Fallo de impresión

El panel inferior **Periféricos** conserva el último resultado de impresión.
Expandirlo permite consultar colas/puertos y actualizar su detección (también se
consulta cada 30 segundos). No realiza impresiones ni abre el cajón por detectar
un cambio. La prueba del lector sólo verifica entrada de texto y no crea ventas.

“Cola detectada” significa instalada en Windows, no impresora encendida.
“Enviado al controlador” no confirma papel impreso. Un timeout puede dejar un
trabajo en resultado incierto: revisar la cola y el papel antes de reimprimir.
No hay cambio automático a otra impresora ni reintento automático del ticket.

1. Confirmar que la venta está guardada antes de reimprimir.
2. Revisar papel, tapa, energía, cable/red y cola de Windows.
3. No repetir la venta ni el cobro para obtener otro ticket.
4. Reimprimir desde Historial usando el mismo folio.
5. En serial/TCP, confirmar destino y timeout; evitar dos destinos activos al mismo
   dispositivo.
6. Si el cajón depende de la impresora, abrirlo sólo con autorización y registrar el
   motivo.
7. Probar texto, acentos, QR y corte; conservar modelo/firmware en el incidente.

Sin impresora configurada se informa el fallo y se conserva la venta. Configure
el nombre exacto de la cola, `COMn@baud` o `tcp://host:puerto` en Ajustes > Hardware
& Tickets. No repetir el cobro para probar un destino. Los destinos seriales
fallidos ya no se desvían a una impresora predeterminada.

Usar comprobante alterno autorizado si el comercio lo permite. Escalar P2 cuando no
exista impresora alternativa o la cola produzca tickets duplicados.
