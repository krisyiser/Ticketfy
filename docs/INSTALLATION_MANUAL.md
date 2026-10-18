# Manual de instalación

## Requisitos previos

- Windows x64 incluido en la matriz soportada.
- Cuenta de Windows dedicada al punto de venta, con contraseña y acceso restringido.
- Disco local NTFS con espacio para aplicación, base y al menos 30 respaldos.
- UPS recomendada para caja, equipo, impresora y red local.
- Fecha, hora y zona horaria correctas.

## Instalación nueva

1. Verificar la firma Authenticode del instalador comercial antes de ejecutarlo.
2. Instalar con una cuenta administradora; operar después con la cuenta dedicada.
3. Iniciar Ticketfy y completar empresa, usuario administrador y licencia.
4. Cerrar y volver a abrir para comprobar persistencia de DPAPI y migraciones.
5. Configurar la ruta de respaldo. Si es unidad externa, validar disponibilidad al
   iniciar y al cerrar turno.
6. Configurar periféricos sólo con perfiles de la matriz; imprimir y escanear pruebas.
7. Crear un respaldo manual y ejecutar la validación de restauración antes de cargar
   datos productivos.

## Actualización

1. Cerrar el turno y crear un respaldo verificado.
2. Registrar versión actual, SHA-256 del paquete y folio final.
3. Aplicar el paquete firmado sin borrar `%LocalAppData%\Ticketfy`.
4. Iniciar una vez con el usuario habitual y esperar las migraciones.
5. Ejecutar venta, reimpresión y corte de prueba.

Si el inicio falla, no reinstalar ni borrar datos: seguir
[fallo de actualización](runbooks/UPDATE_FAILURE.md).

## Desinstalación y reinstalación

Desinstalar el ejecutable no autoriza borrar la carpeta de datos. Antes de cualquier
limpieza se requieren dos respaldos verificados en ubicaciones distintas y registro de
la llave maestra custodiada para recuperación en otro equipo.
