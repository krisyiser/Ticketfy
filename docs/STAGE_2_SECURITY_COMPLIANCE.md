# Etapa 2 — Seguridad y cumplimiento

Fecha de corte: 18 de octubre de 2026.

## Alcance

Esta etapa endurece el producto de escritorio y su operación local. Facturama, el servidor, el sitio de descargas y Forgejo continúan fuera del alcance operativo por decisión del proyecto. Los controles fiscales descritos aquí son requisitos de diseño para la reactivación posterior del módulo fiscal; no implican certificación legal ni fiscal.

## Controles implementados

| Área | Control | Evidencia técnica |
|---|---|---|
| Secretos | Llave de base, llave HMAC, estado de licencia y credenciales protegidos por DPAPI en alcance `CurrentUser`; importación única de la llave heredada y fallo cerrado ante corrupción | `Services/Security/DpapiSecretStore.cs`, `SecurityManager.cs`, `SecureCredentialStore.cs` |
| Contraseñas y PIN | PBKDF2-HMAC-SHA256 a 600,000 iteraciones; salt aleatorio; comparación constante; rehash progresivo de SHA-256 y PBKDF2 antiguo; migración del PIN en claro al primer uso válido; pistas de contraseña heredadas eliminadas | `Services/Security/SecurityManager.cs`, `AuthService.cs`, `Core/Repositories/UserRepository.cs` |
| RBAC | Catálogo central, presets explícitos, claves desconocidas y roles desconocidos denegados por defecto, overrides persistidos sólo para permisos registrados | `Core/Security/PermissionCatalog.cs`, `Core/Services/UserRolePermissionEngine.cs` |
| Auditoría | Cadena HMAC-SHA256, llave DPAPI, verificación periódica, triggers SQLite que impiden `UPDATE` y `DELETE`, conservación local sin exportación CSV en claro | `Services/Audit/AuditService.cs`, migración `20261018190000_AddAuditIntegrityChain` |
| Licenciamiento | Evaluación persistente de 30 días, detección de retroceso de reloj, JWT RSA-SHA256 con issuer/audience/expiración, vínculo a dispositivo, renovación atómica y uso sin conexión hasta `exp` | `Services/Security/LicenseEnforcementService.cs`, `Ticketfy.Keygen/Program.cs` |
| Registros | Redacción de secretos, JWT, correo, RFC, CURP y PAN; seudonimización de usuarios de autenticación; crash log sanitizado | `Logging/LogSanitizer.cs`, `Logging/SanitizingTextFormatter.cs` |
| Distribución | El build de release exige certificado, sello de tiempo SHA-256, firma de ejecutables e instaladores y verificación con `signtool`; los certificados autofirmados se limitan expresamente a desarrollo | `build_release.ps1`, `create_cert.ps1` |

## Política operativa de licencia

- Una instalación sin licencia inicia una evaluación local de 30 días, persistida y protegida con DPAPI.
- La evaluación expirada, una licencia caducada/alterada, un dispositivo distinto o un retroceso de reloj bloquean la operación.
- Una licencia comercial debe incluir `device_id`, `license_type`, `sub`, `jti`, `iat`, `nbf` y `exp`, y estar firmada con RSA-SHA256.
- No se necesita conexión durante la vigencia firmada. La renovación sustituye el token local de manera atómica.
- La llave privada de emisión nunca forma parte del repositorio, instalador ni equipo del cliente.

## Matriz mexicana de cumplimiento

### CFDI, conservación y cancelaciones

- Conservar contabilidad, comprobantes y documentación relacionada durante el plazo fiscal aplicable. Como línea base técnica se adopta un mínimo de cinco años, sujeto a excepciones y plazos especiales del caso concreto. Fuente primaria: [Código Fiscal de la Federación, artículo 30 — SAT](https://wwwmat.sat.gob.mx/articulo/62186/articulo-30).
- Preservar el XML original del CFDI, acuses, relaciones de sustitución, solicitudes/respuestas de cancelación y evidencia de aceptación o rechazo; un PDF no sustituye al XML fiscal.
- El futuro módulo fiscal debe manejar los motivos de cancelación `01` a `04`, la relación con el CFDI sustituto cuando corresponda y el flujo de aceptación del receptor. Fuente primaria: [Proceso de cancelación de factura electrónica — SAT](https://www.sat.gob.mx/minisitio/Factura/cancela_procesocancelacion.htm?trk=article-ssr-frontend-pulse_little-text-block).
- Las reglas y excepciones vigentes deben revisarse contra la resolución anual antes de cada release fiscal. Referencia vigente al corte: [Resolución Miscelánea Fiscal 2026 — SAT](https://www.sat.gob.mx/minisitio/NormatividadRMFyRGCE/documentos2026/rmf/rmf/RMF_2026-DOF-28122025.pdf).

### Datos personales

- Antes de comercializar debe existir aviso de privacidad con identidad y domicilio del responsable, categorías de datos (incluidos sensibles), finalidades, medios para limitar uso/divulgación, procedimiento ARCO y mecanismo para comunicar cambios.
- Debe definirse responsable interno, canal ARCO, plazos de atención, registro de solicitudes, contratos con encargados, respuesta a incidentes y eliminación segura al concluir retención u obligaciones legales.
- Aplicar minimización: el POS no debe registrar datos personales que no sean necesarios para venta, crédito, fidelidad o la obligación fiscal correspondiente.
- La base jurídica se revisó contra la [Ley Federal de Protección de Datos Personales en Posesión de los Particulares publicada el 20-03-2025 — DOF](https://www.dof.gob.mx/nota_detalle.php%3Fcodigo%3D5752569%26fecha%3D20/03/2025), en particular aviso de privacidad y derechos ARCO. Las medidas administrativas, técnicas y físicas deben documentarse conforme a la [recomendación oficial de seguridad de datos — DOF](https://dof.gob.mx/nota_detalle_popup.php?codigo=5320179).

## Retención y eliminación

- Datos fiscales y evidencia de operaciones: mínimo técnico de cinco años; ampliar cuando exista obligación especial, auditoría, litigio o requerimiento de autoridad.
- Auditoría administrativa y transaccional: cinco años por defecto; no hay borrado automático. La purga futura debe exigir política aprobada, respaldo verificable, vencimiento de retención y ausencia de legal hold.
- Logs operativos sanitizados: 30 archivos diarios. No deben emplearse como archivo fiscal ni contener secretos o datos personales completos.
- Credenciales: eliminar al revocar la integración; nunca copiar a respaldos en texto claro.

## Evidencia de verificación

- Suite Release Windows: 67/67 pruebas aprobadas.
- Compilación: 0 advertencias y 0 errores.
- Auditoría NuGet: sin paquetes vulnerables conocidos en las fuentes configuradas.
- Pruebas específicas: DPAPI, rehash heredado, migración de PIN, deny-by-default RBAC, cadena y triggers de auditoría, sanitización, evaluación/licencia y reloj.

## Condiciones externas para cerrar la etapa

La implementación interna está preparada, pero el criterio de salida completo no puede declararse sólo con una autoevaluación. Antes de considerar cerrada la Etapa 2 se requieren:

1. Certificado comercial de firma de código emitido por una CA, custodiado fuera del repositorio, y un release reproducible firmado y verificado.
2. Revisión independiente de seguridad (código, binarios, configuración, amenaza local y pruebas de penetración) sin hallazgos críticos o altos abiertos.
3. Dictamen de abogado de privacidad y contador/asesor fiscal mexicano sobre aviso, ARCO, retención, CFDI y cancelaciones.
4. Reactivar y revisar Facturama por separado cuando vuelva el acceso al servidor; mientras tanto no se declara cumplimiento fiscal automatizado de ese módulo.

Este documento es una matriz técnica y no sustituye asesoría legal, fiscal ni una auditoría independiente.
