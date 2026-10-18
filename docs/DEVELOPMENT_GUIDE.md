# Valcore Desktop Development Guide

## 1. Development Mandates

### Separation of Concerns
Monolithic code is strictly prohibited. You must decouple domain business logic, presentation ViewModels, EF Core data layers, and AXAML UI view components into strict directory boundaries.

### MVVM Pattern
- Code-behind files (`.axaml.cs`) must contain **zero business logic**.
- Use `CommunityToolkit.Mvvm` for all data bindings and observable properties.
- Use explicit bindings (`{Binding Path}`) instead of code-behind manipulation.

### UI Design (Industrial Flow)
- **Zero-Mouse First:** Complex views must be 100% operable via keyboard shortcuts (F1-F12, Enter, Esc, Navigation Keys).
- **Density:** Maximize screen space. High-density data grids, compact padding, and tight typography scales.
- **Colors:** Deep background tones (Slate/Zinc). Single-tone muted functional accents. NO neon gradients.

## 2. Compilation & Deployment
Use the .NET 10 SDK pinned in `global.json` and restore the repository-local EF
tool. Release publishes a self-contained, multi-file Windows application without
trimming or Native AOT. Do not enable those options without a separate compatibility
validation of Avalonia, SQLCipher and reflection-dependent libraries.

```powershell
dotnet tool restore
./eng/invoke-quality-gate.ps1 -LockedRestore -VerifyReproducible
```

The gate creates unsigned test payloads without launching the application.
`Ticketfy.Desktop/build_release.ps1` is the separate signed-installer workflow and
requires signing credentials. See [.NET 10 migration](NET10_MIGRATION.md) for the
isolated local SDK, test evidence and release limitations. Never run tests against
the operational database or the operator's encryption-key store.

## 3. Hardware Interfacing
Always offload hardware calls (ESC/POS Printing, COM Port scanning) to `Task.Run()` or dedicated asynchronous background threads. NEVER execute raw hardware I/O on the Avalonia UI Thread.

## 4. Git Protocol
- CI targets `main`; a branch name does not certify commercial readiness.
- Single-line imperative commit messages under 50 chars. (e.g., `feat: establish local sqlite audit log engine`).
