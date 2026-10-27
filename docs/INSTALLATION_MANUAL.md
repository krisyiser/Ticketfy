# Manual de instalación

## Instalador para pruebas internas

El candidato compartible se llama `Ticketfy-Pruebas-<versión>-<commit>-win-x64-Setup.exe`. Se genera desde un paquete de commit limpio con arranque verificado. Su ZIP `*-Compartir.zip` incluye el EXE, SHA256, instrucciones y manifiesto; no contiene bases, llaves, usuarios ni licencias. El candidato interno no está firmado: Windows o la política corporativa pueden advertir o bloquearlo. La aprobación de TI no debe sustituirse por desactivar protecciones.

Requiere Windows 10 **22H2/build 19045** o Windows 11 **x64 Intel/AMD**; bloquea sistemas anteriores, Windows de 32 bits y ARM. Incluye .NET y dependencias de aplicación; no exige SDK, Visual Studio ni descarga de runtime. La instalación es por usuario, sin elevación. La matriz distingue el objetivo de compatibilidad de la aceptación física en cada equipo.

1. Extraer el ZIP y comprobar el EXE con `Get-FileHash -Algorithm SHA256`, comparándolo con el archivo `.sha256` recibido por el canal de confianza del proyecto.
2. Cerrar Ticketfy de pruebas e instalar. Su identidad y registro de desinstalación son distintos de los de la edición comercial.
3. Abrir **Ticketfy - Pruebas internas** desde Inicio o escritorio. Incluso el ejecutable directo usa `%LocalAppData%\Ticketfy.InternalTesting\Data`; no selecciona la carpeta habitual `%LocalAppData%\Ticketfy`.
4. Crear empresa y usuarios ficticios. Se conserva la política normal de evaluación; no se incluyen claves universales ni licencias de prueba falsificadas.
5. Ejecutar la jornada incluida en `LEEME-PRUEBAS.txt`. Cada compañero trabaja con su propia base local; no se comparte el directorio por red.

El programa se instala en `%LocalAppData%\Programs\Ticketfy.InternalTesting`. Reinstalar conserva los datos; desinstalar retira el programa y accesos directos y conserva bases, llaves y respaldos. Cerrar turnos y verificar un respaldo antes de actualizar. No ejecutar una versión anterior sobre un esquema migrado por otra posterior. Facturama y la infraestructura remota permanecen pausados.

Para desarrolladores, desde la raíz del repositorio:

```powershell
./eng/build-internal-installer.ps1 -PackageDirectory ./artifacts/<gate>/package-a/publish-win-x64 -OutputDirectory ./artifacts/<salida-nueva>
./eng/assert-internal-installer.ps1 -InstallerDirectory ./artifacts/<salida-nueva>
```

Requiere Inno Setup 6.4 o posterior. La segunda orden instala en una carpeta nueva del laboratorio, comprueba archivos y accesos directos, inicia con datos sintéticos, rechaza la actualización con el proceso abierto y verifica reinstalación/desinstalación sin alterar datos. Se niega a reemplazar una edición interna o accesos directos ya existentes. La evidencia no homologa periféricos ni reemplaza la jornada manual. CI conserva el instalador y su reporte de verificación.

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
