# Modo de desarrollo local

Desde el 18 de octubre de 2026, Ticketfy se compila, prueba y conserva en este equipo mientras la infraestructura remota permanece en mantenimiento.

## Integraciones pausadas

- Facturama y la emisión de CFDI.
- Registro de dispositivos, telemetría, heartbeat y sincronización remota de licencias.
- Búsqueda y descarga automática de actualizaciones.
- Telemetría de errores y consultas lentas hacia Sentry; esos eventos se escriben en el registro local.
- Publicación en el servidor, el sitio de descargas y Forgejo.

La política se centraliza en `Ticketfy.Desktop/Core/Configuration/LocalDevelopmentMode.cs`. Los valores están deshabilitados en código para que una variable de entorno residual no reactive tráfico de producción accidentalmente.

## Flujo autorizado actualmente

1. Restaurar y compilar la solución localmente.
2. Ejecutar la suite de pruebas local.
3. Generar instaladores con `Ticketfy.Desktop/build_release.ps1` cuando sea necesario.
4. Conservar los artefactos en `Ticketfy.Desktop/Output/Releases`, fuera del control de versiones.

`build_release.ps1` genera `local-build.json`, no copia el sitio web, no firma con un servidor de sello de tiempo y no ejecuta despliegues. Los scripts de publicación remota se conservan para una futura reactivación, pero terminan antes de abrir cualquier conexión.

## Reactivación futura

La infraestructura no debe reactivarse cambiando solamente una bandera. Antes se debe:

1. Rotar las credenciales y llaves históricamente expuestas.
2. Verificar TLS, claves de host SSH y permisos del repositorio remoto.
3. Completar y probar el módulo de Facturama en sandbox.
4. Agregar pruebas de contrato para licencias, manifiestos y descargas.
5. Revisar y habilitar deliberadamente cada ruta bloqueada, junto con sus pruebas.
