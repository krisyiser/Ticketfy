# Etapa 4 — Calidad automatizada

Fecha de corte: 18 de octubre de 2026.

## Estado

La canalización está registrada para GitHub Actions en `.github/workflows/quality.yml`.
La plataforma actual es .NET 10 LTS; su validación se detalla en
[Migración a .NET 10](NET10_MIGRATION.md). La ejecución remota y el requisito de
checkout limpio deben verificarse para cada candidato.
La definición equivalente de Forgejo queda conservada, pero inactiva hasta recuperar
el servidor.

Facturama real, webhooks públicos, servidor de descargas y publicación remota siguen
fuera de alcance conforme al modo de desarrollo local. Las pruebas fiscales usan un
proveedor controlado para validar la cola y sus fallos sin emitir comprobantes reales.

## Puerta de calidad

`eng/invoke-quality-gate.ps1` ejecuta, en orden:

1. comprobación del SDK fijado, restauración Release NuGet con archivos
   `packages.lock.json`, opción `--locked-mode` y herramientas locales fijadas;
2. verificación de formato de Desktop, pruebas y Keygen;
3. compilación Release con advertencias como errores y analizadores del SDK;
4. detección de modelos sin migración en ambos contextos y suite xUnit/VSTest con
   pruebas Avalonia Headless;
5. cobertura Cobertura y umbral crítico agregado de 75%;
6. auditoría JSON de vulnerabilidades directas y transitivas, sin restaurar otra
   configuración sobre los assets probados;
7. publicación self-contained `win-x64` y ZIP determinista;
8. segunda publicación opcional y comparación exacta de SHA-256;
9. comprobación opcional de que el checkout empezó y terminó limpio.

La CI usa exactamente este script. No existe una segunda definición de los pasos que
pueda divergir de la validación local.

## Cobertura

Las cifras de este documento corresponden a la validación de la Etapa 4. Para el
árbol de trabajo actual, consultar la evidencia de [Etapa 6](STAGE_6_COMMERCIAL_FOUNDATION.md).
Su gate amplía el núcleo evaluado y exige también 70% por servicio, excepto el
inicializador de base, que conserva la evaluación agregada.

La cobertura global de la aplicación Avalonia es informativa: incluye vistas,
adaptadores de hardware y rutas que requieren laboratorio. El criterio bloqueante es
el agregado explícito del núcleo comercial en `eng/assert-critical-coverage.ps1`.
No se excluyen `async` ni sus máquinas de estado.

Resultado verificado:

| Área/archivo | Cobertura de líneas |
|---|---:|
| Respaldo | 90.80% |
| Pagos/deuda de cliente | 64.24% |
| Migración y adopción heredada | 55.64% |
| Cola fiscal | 75.42% |
| Terminal Mercado Pago | 70.31% |
| Catálogo/inventario | 85.37% |
| Cancelaciones y devoluciones | 81.55% |
| Venta principal | 70.57% |
| Inventario transaccional | 96.55% |
| Recuperación de cobro | 76.54% |
| Caja/turnos | 91.76% |
| **Núcleo crítico agregado** | **76.32% (1,428/1,871)** |

Los porcentajes por archivo ayudan a dirigir el trabajo siguiente; el umbral comercial
es agregado porque una operación cruza venta, inventario, deuda, caja y diario en una
sola transacción.

## Fallos y regresiones cubiertos

| Riesgo | Comportamiento verificado |
|---|---|
| Disco lleno | El respaldo comprueba espacio antes de abrir el destino y no deja archivo parcial |
| DB bloqueada | Sólo `SQLITE_BUSY` se reintenta, con cuatro intentos totales y espera acotada |
| Apagado | Un cobro autorizado se recupera una sola vez; una operación fiscal interrumpida libera lease y vuelve a `WaitingRetry` |
| Red intermitente | Fiscal reintenta sólo fallos confirmados como seguros; pago reutiliza referencia e idempotency key |
| Doble clic | Dos guardados concurrentes de la misma venta producen una venta, un descuento de stock, un movimiento de caja y un diario |
| Migraciones | Se actualiza cada versión comercial soportada y se rechaza un esquema heredado incompleto antes de adoptar historial |
| UI | Login y ticket/cobro se cargan con controles, bindings y comandos reales mediante Avalonia Headless |

## Reproducibilidad y firma

El paquete de CI es deliberadamente un payload **sin firma**. Su objetivo es demostrar
que el mismo commit produce los mismos bytes antes de la firma. La firma Authenticode
y el instalador comercial permanecen en `Ticketfy.Desktop/build_release.ps1`, porque
el timestamp de una autoridad externa hace que el artefacto firmado no sea
bit-a-bit reproducible.

En la verificación local se construye dos veces el payload `3.1.56 win-x64` y la puerta
exige que ambas copias produzcan el mismo SHA-256. El valor se informa durante cada
ejecución y no se fija en el repositorio porque incorpora la identidad del commit.

## Resultado de la ejecución local

- Suite Release Windows: **93/93** pruebas aprobadas, 0 omitidas.
- Pruebas UI headless: **2/2** aprobadas.
- Compilación Desktop/Tests y Keygen: **0 advertencias, 0 errores**.
- Núcleo crítico: **76.32%**, sobre el mínimo bloqueante de 75%.
- Dependencias Desktop, Tests y Keygen: sin vulnerabilidades conocidas en las fuentes NuGet configuradas.
- Empaquetado determinista: dos ejecuciones, hashes SHA-256 idénticos.

## Criterio de salida

Para declarar la etapa cerrada se requiere:

1. confirmar todos los cambios de las etapas 0–4 en Git;
2. ejecutar `eng/invoke-quality-gate.ps1 -LockedRestore -RequireClean -VerifyReproducible`
   desde ese commit limpio;
3. obtener una ejecución verde del workflow de GitHub Actions sin modificar sus pasos;
4. conservar el reporte Cobertura y el hash del paquete como evidencia del release candidate.

Las pruebas reales de cuentas y hardware pendientes de la Etapa 3 siguen siendo un
requisito independiente para declarar un release comercial.
