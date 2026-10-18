# Migración de Ticketfy a .NET 10 LTS

Desktop, pruebas y Keygen utilizan `net10.0`. La validación se realiza con bases
sintéticas, llaves desechables y servicios externos bloqueados. Este cambio de
plataforma no autoriza actualizar la base operativa ni publicar un release comercial.

## Versiones y alcance

| Componente | Versión fijada |
|---|---|
| SDK | 10.0.402 en `global.json`, sin roll-forward ni previews |
| Runtime autocontenido | 10.0.13, incluido por el SDK de publicación |
| EF Core SQLite Core, Design y herramienta local dotnet-ef | 10.0.13 |
| Microsoft.Extensions.Http, System.IO.Ports y System.Management | 10.0.13 |
| Microsoft.NET.Test.Sdk | 18.11.0 |
| xUnit y adaptador Visual Studio | 2.9.3 y 4.0.1 |
| Coverlet collector | 10.1.0 |

Se regeneraron los tres archivos `packages.lock.json`. Avalonia, Semi y los demás
componentes no relacionados conservan sus versiones; esta entrega no supone una
actualización mayor de toda la interfaz. Se mantiene el paquete SQLite **Core** y
`SQLitePCLRaw.bundle_e_sqlcipher`, sin introducir una segunda biblioteca SQLite nativa.
Las herramientas de diagnóstico Avalonia se excluyen de Release, conservando un
grafo de paquetes estable entre configuraciones. Desktop y pruebas declaran
`win-x64` y `win-x86` como identificadores de runtime para que restaurar Debug o
publicar otra arquitectura no reescriba los lockfiles.

El SDK y runtime seleccionados corresponden a la
[publicación oficial de .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
Las actualizaciones futuras de seguridad requieren revisar el pin del SDK, EF,
dependencias y lockfiles, y volver a ejecutar la puerta de calidad; un paquete
autocontenido no se actualiza instalando otro runtime en el equipo del operador.

## Compilar en este equipo

El SDK descargado está aislado bajo `artifacts/dotnet10-sdk`, fuera del control de
versiones. Su ZIP se comprobó contra el SHA-512 publicado en los metadatos oficiales
de Microsoft. No sustituye la instalación global de .NET 9.

Desde la raíz del repositorio, en PowerShell 7:

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location) 'artifacts/dotnet10-sdk'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
dotnet tool restore
./eng/invoke-quality-gate.ps1 -LockedRestore -VerifyReproducible
```

En otro equipo, instalar el SDK exacto de `global.json` antes de compilar. Las
variables anteriores sólo afectan a esa sesión. GitHub Actions lo instala desde
el mismo archivo; no utiliza una versión flotante `10.0.x`.

El gate se ejecuta desde un checkout limpio con `-RequireClean` para aprobar un
candidato. Las salidas de cada ejecución quedan en un directorio nuevo
`artifacts/quality-<identificador>`: TRX, cobertura, auditorías NuGet y paquetes.
No se borran paquetes anteriores ni se reutiliza un reporte de cobertura antiguo.

Para comprobar las otras configuraciones sin ejecutar el POS:

```powershell
dotnet build Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj -c Debug -p:RestoreLockedMode=true
dotnet publish Ticketfy.Desktop/Ticketfy.Desktop.csproj -c Release -r win-x86 --self-contained true --output artifacts/net10-x86-publish -p:RestoreLockedMode=true
```

Use un directorio de salida nuevo al repetir la publicación para conservar cada
evidencia. La prueba x86 de esta migración es de compilación/publicación, no una
certificación de ejecución de periféricos o de Windows de 32 bits.

## Compatibilidad y pruebas

Los modelos de `AppDbContext` y `AuditDbContext` se comprueban con la herramienta EF
local y sus fábricas de diseño, sin conectarse a las bases operativas. El cambio a
EF 10 no requiere una migración de esquema adicional. Se conservan los identificadores,
metadatos históricos y triggers de las migraciones previas; no se reescribe su origen
EF 9 para aparentar que fueron creadas con EF 10.

`Net10MigrationTests` agrega diez casos: actualización repetible desde los siete
esquemas registrados, migración de auditoría, fechas SQLite y ciclo cifrado de
creación, reapertura, respaldo y restauración de prueba. El ciclo verifica importes,
stock, historial de migraciones y rechazo de lectura sin llave. Complementa las
pruebas existentes de actualización con importes heredados, idempotencia, caja,
compras, pagos y UI headless. Los esquemas históricos son sintéticos: no equivalen
a una prueba sobre una copia representativa de cada instalación comercial.

Microsoft.Data.Sqlite 10 modifica el tratamiento de fechas con offset. La regresión
comprueba la lectura UTC de un offset explícito y que una fecha local heredada sin
offset conserve su hora al usar el conversor comercial. No se activa el interruptor
global que restaura el comportamiento antiguo. Referencia:
[cambios incompatibles de EF y SQLite 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes).

Las pruebas conservan VSTest explícitamente en `global.json` y no activan MTP, que
no admite este recolector. Véase la
[compatibilidad de Coverlet collector](https://www.nuget.org/packages/coverlet.collector/10.1.0).
Las pruebas de secretos y respaldo ya no llaman a `SecurityManager.GetMasterKey`:
usan llaves propias y archivos temporales, sin leer/importar el almacén del operador.

## CI y empaquetado

La restauración utiliza explícitamente Release y `win-x64`, incluidos los runtime
packs autocontenidos. La auditoría NuGet analiza dependencias directas y transitivas
en JSON y utiliza `--no-restore` para conservar los assets ya probados. Esta opción
evita la [restauración implícita introducida en .NET 10](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list).
Las advertencias siguen tratándose como errores y los umbrales de cobertura se
mantienen: 75% agregado y 70% por servicio financiero, con la excepción documentada
del inicializador de base.

El manifiesto del paquete registra SDK, framework, runtime incluido, versión,
commit y estado limpio/sucio. La comparación SHA-256 corresponde al payload sin
firma; no certifica un instalador comercial firmado. Inno Setup recibe rutas y
versión por parámetros, sin rutas absolutas de este equipo ni edición del fuente
durante el empaquetado.

Facturama, servidor y Forgejo permanecen aplazados. Sólo se sincroniza el pin de SDK
en la definición conservada de Forgejo; no se conecta ni se ejecuta allí. La CI de
GitHub requiere una ejecución remota posterior desde el commit aprobado.

## Resultados locales del 18 de octubre de 2026

| Verificación | Resultado |
|---|---|
| Gate completo con `CI=true`, `-LockedRestore -VerifyReproducible` | Aprobado |
| Suite Release x64 | 202 aprobadas, 0 fallidas, 0 omitidas |
| Cobertura de los 26 archivos críticos | 87,66%, 2.835 de 3.234 líneas |
| Compilación Desktop y pruebas en Debug y Release, Keygen en Release | 0 advertencias, 0 errores |
| Formato y análisis del SDK | Aprobados, sin reducir severidades |
| Modelos comercial y auditoría | Sin diferencias pendientes de migración |
| Auditoría NuGet de los tres proyectos, incluidas transitivas | Sin vulnerabilidades conocidas reportadas por la fuente configurada |
| Publicación autocontenida x86 | Generada con runtime 10.0.13; no ejecutada |
| Paquetes autocontenidos x64 | Dos ZIP con SHA-256 idéntico y runtime 10.0.13 |
| Lockfiles entre Debug, x86 y gate Release x64 | Hashes sin cambios |
| Inno Setup x64 | Compilación validada; instalador sin firma, no ejecutado |

La evidencia final se conserva bajo
`artifacts/quality-999510ea16624fc4ba158496de6519f7`:

- `TestResults/quality.trx` y los tres informes `*-dependencies.json`.
- `TestResults/0f027ad0-c389-414e-8957-dd1ce9840b24/coverage.cobertura.xml`.
- `package-a/` y `package-b/`, con ZIP y manifiestos de publicación.

SHA-256 común de los ZIP:
`716E0923AF5930AF4DBCD93DA5CD77782E2C8B2E311C6CBD4BCC33E18FE9521B`.

El payload x86 está en `artifacts/net10-x86-publish`. El instalador experimental
está en `artifacts/net10-installer-validation`; no debe distribuirse como release.
Los binarios y el SDK permanecen ignorados por Git.

El árbol conserva cambios previos sin commit y los manifiestos indican
`source_state: dirty`. Por ello no se utilizó `-RequireClean` ni se afirma una
release desde un commit limpio. Quedan pendientes esa ejecución, la CI remota,
firma comercial y pruebas de instalación/actualización autorizadas sobre una
copia representativa. No se abrió ni migró la base operativa, ni se reactivaron
Facturama o la infraestructura remota.
