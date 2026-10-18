# Ticketfy Desktop POS

Current automated-quality status: [Stage 4 — Automated quality](docs/STAGE_4_AUTOMATED_QUALITY.md).

Ticketfy is a high-performance, industrial-grade Point of Sale (POS) and ERP system built on **Protocol Valcore v4.0**.

![Valcore Badge](https://img.shields.io/badge/Architecture-Valcore_v4.0-blue?style=flat-square)
![Avalonia UI](https://img.shields.io/badge/UI-Avalonia-purple?style=flat-square)
![.NET 9](https://img.shields.io/badge/Runtime-.NET_9-512BD4?style=flat-square)

## Overview
Ticketfy is designed to provide zero-network local resilience, utilizing an offline-first encrypted SQLite database and direct hardware integration for high-speed retail checkout operations. It strictly enforces the MVVM pattern via `CommunityToolkit.Mvvm` and features an ultra-dense, keyboard-first industrial user interface.

## Documentation
Please refer to the `docs/` folder for comprehensive architectural guidelines:
- [Architecture & Structure](docs/ARCHITECTURE.md)
- [Development Guide & Mandates](docs/DEVELOPMENT_GUIDE.md)
- [Security & Release Operations](docs/SECURITY_AND_RELEASES.md)
- [Local Development Mode](docs/LOCAL_DEVELOPMENT_MODE.md)
- [Stage 1: Transactional Core](docs/STAGE_1_TRANSACTIONAL_CORE.md)
- [Stage 2: Security and Compliance](docs/STAGE_2_SECURITY_COMPLIANCE.md)
- [Stage 3: Commercial Integrations](docs/STAGE_3_COMMERCIAL_INTEGRATIONS.md)
- [Stage 4: Automated Quality](docs/STAGE_4_AUTOMATED_QUALITY.md)

## Requirements
- .NET 9.0 SDK
- Windows 10/11 (for full hardware POS compatibility)

## Build Instructions
For development:
```bash
dotnet build Ticketfy.Desktop
```

For a local release candidate:
Use the PowerShell script to produce local x64 and x86 artifacts. It does not publish to the server, download site or Forgejo while those systems are in maintenance.
```powershell
./Ticketfy.Desktop/build_release.ps1
```

For the complete local quality gate (locked restore, format, build, tests,
critical coverage, dependency audit and deterministic package):
```powershell
./eng/invoke-quality-gate.ps1 -LockedRestore -VerifyReproducible
```

## Current integration status

Facturama, device registration, remote license synchronization, automatic updates, the download site and Forgejo publishing are intentionally disabled. The POS core, local database, backups, build and tests remain active on this workstation.

## Licensing & Security
This software uses RSA-signed JWTs for offline license verification. Hardcoded secrets are explicitly prohibited.
The private signing key must remain outside this repository. `Ticketfy.Keygen` accepts its path at runtime and emits licenses with validated issuer, audience and expiration claims.

## Release artifacts

Installers, portable ZIPs and NuGet/Velopack packages are generated under
`Ticketfy.Desktop/Output/Releases`. They stay local and are not committed to this source repository.
