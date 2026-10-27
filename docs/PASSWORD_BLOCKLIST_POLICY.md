# Referencia local de contraseñas comunes

La política 2 añade una lista pública verificable a los controles de longitud, identidad, secuencias y ejemplos previsibles de Ticketfy. La consulta se realiza dentro del proceso: no envía contraseñas, prefijos ni huellas a un proveedor. Sólo compara la contraseña completa; una frase válida puede contener palabras presentes en la lista. Este criterio de comparación sigue [NIST SP 800-63B-4, sección 3.1.1.2](https://pages.nist.gov/800-63-4/sp800-63b.html#passwordver).

## Referencia y procedencia

Se utiliza el archivo público `100k-most-used-passwords-NCSC.txt` de [SecLists, revisión fija](https://github.com/danielmiessler/SecLists/blob/1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56/Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt). El nombre del archivo no indica cobertura completa, actualidad garantizada ni certificación de NCSC para Ticketfy.

| Dato | Valor |
|---|---|
| Revisión de origen | `1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56` |
| SHA256 del archivo original | `C2E5696882C603B76BB67A47EE970897E5A76FC4C3F5547ABE3D0CA340C576E0` |
| Tamaño original | 835.538 bytes |
| Registros no vacíos | 99.839; 331 tienen al menos 15 unidades de texto |
| Transformación para comparación | Quitar espacios de extremos, NFC, mayúsculas invariantes, SHA256 UTF-8, deduplicar y ordenar ordinalmente |
| Huellas únicas incluidas | 97.746 |
| Formato del recurso | ASCII hexadecimal mayúsculo, 64 caracteres y LF por registro |
| Tamaño del recurso | 6.353.490 bytes |
| SHA256 del recurso | `3C36D23FA8D5A5AE79F1AA6188B8BFE4B4E18EF4E23DB11F37EC47338BA8989A` |

Se conservan también los ejemplos cortos para detectar un valor común rellenado con espacios hasta superar el mínimo. La normalización se usa únicamente para comparación; la verificación y almacenamiento de credenciales conserva el texto literal y el esquema PBKDF2 existente. Las huellas SHA256 del recurso describen ejemplos públicos y no son hashes de autenticación ni datos de cuentas del comercio.

La [licencia MIT de SecLists](https://github.com/danielmiessler/SecLists/blob/1a7bb9127eca9e6ff2fc0301c597fe6e16a0cb56/LICENSE) se conserva en `Ticketfy.Desktop/ThirdPartyNotices/SecLists-LICENSE.txt` y se distribuye en el paquete. El recurso se incorpora al ensamblado; no se carga una lista reemplazable desde la carpeta de datos.

## Integridad y transición

Antes de inicializar las bases, el arranque comprueba presencia, tamaño, SHA256, formato, cantidad y orden estricto del recurso. Una referencia ausente, truncada, alterada o duplicada detiene el arranque. El lector conserva aproximadamente 3,13 MB de huellas binarias y busca por orden, sin cargar cadenas de cien mil contraseñas en cada autenticación. Esa cifra describe el arreglo retenido, no la memoria total del proceso.

Altas, configuración inicial, cambios administrativos y cambios del propietario comparten la política. Una cuenta de la política 1 necesita comprobar su contraseña antes de obtener acceso por PIN. Una contraseña aceptable obtiene la versión 2 al autenticarse; no se exige cambiarla por el mero incremento de versión. Una contraseña bloqueada requiere reemplazo y no obtiene sesión ni aprobación de supervisor. El cambio conserva la verificación anterior, la transacción, el diario sin secretos y la invalidación del PIN previo. No se migran hashes ni se acepta una contraseña sin verificarla. No hay cambio de esquema de base por esta política.

## Reproducir y mantener

Para reproducir, descargar **el archivo de la revisión indicada** a una carpeta de artefactos y ejecutar con PowerShell 7 y .NET 10:

```powershell
./eng/build-password-blocklist.ps1 -SourceFile './artifacts/password-blocklist-source/100k-most-used-passwords-NCSC.txt' -OutputDirectory './artifacts/blocklist-reproduced'
```

El generador rechaza una fuente con otra huella y no realiza descargas. Comparar cantidad, tamaño y SHA256 de salida con esta tabla. `.gitattributes` conserva LF para que el checkout Windows mantenga el recurso verificable. La construcción normal y la CI utilizan el recurso versionado y no requieren consultar SecLists.

Desarrollo debe revisar procedencia, licencia, cambios y utilidad de la referencia antes de cada edición comercial y cuando haya evidencia de patrones nuevos relevantes. Una actualización exige fijar la nueva revisión y huellas, regenerar el recurso, incrementar la versión de política, actualizar esta ficha y aprobar las regresiones de altas, autenticación, PIN, transición, corrupción del recurso y empaquetado. Nunca actualizar automáticamente la referencia durante un cobro ni editarla en una instalación operativa. Los responsables de seguridad y producto deben aceptar la selección y su periodicidad para la edición ofrecida.

Esta selección acota una exposición conocida, pero no contiene todas las contraseñas comprometidas ni sustituye límites de intentos, almacenamiento seguro, custodia del equipo o revisión independiente. No se declara cumplimiento integral con NIST por incorporar este control.
