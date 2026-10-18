# Privacidad y telemetría operativa

## Principios

La telemetría está desactivada por defecto y requiere consentimiento explícito del
administrador. En el modo local actual no se transmite información a ningún servidor.

Cuando se activa sólo se guarda:

- fecha y hora UTC;
- nombre técnico de un evento permitido;
- resultado exitoso o fallido.

No se recopilan ventas, importes, productos, clientes, empleados, RFC, CURP, correo,
direcciones, PAN, PIN, contraseñas, tokens, licencia, número de serie, nombre de equipo,
usuario de Windows ni identificadores persistentes. Los archivos rotan a 1 MiB y se
eliminan a los 30 días. Revocar el consentimiento elimina inmediatamente el buffer.

Una futura transmisión remota exige una revisión separada, aviso de privacidad,
finalidad, base de consentimiento, endpoint documentado, cifrado en tránsito,
retención, mecanismo de revocación y contrato con el encargado. No basta con activar
el módulo local existente.

El diagnóstico exportado es una acción separada iniciada por el usuario. Sanitiza
secretos y datos estructurados conocidos; el operador debe revisarlo antes de
compartirlo y usar únicamente el canal de soporte autorizado.
