# Security and release operations

## Required secret handling

Secrets must never be committed to this repository, passed as command-line
arguments, or written to release manifests.

- `TICKETFY_API_KEY`: authorizes device registration. Registration is skipped
  when it is not configured.
- `TICKETFY_SIGNING_CERT_PASSWORD`: unlocks the local Authenticode PFX during
  release signing.
- `TICKETFY_DEPLOY_PASSWORD`: optional SSH password for the deployment scripts.
  Prefer an SSH agent and a key protected by the operating system.
- Mercado Pago device ID and API token are configured in application settings.
  Payment processing is rejected when either value is missing.

Remote deployment is currently disabled. When it is restored, it must require a
previously trusted SSH host key and reject unknown hosts.

## License signing

The RSA private signing key must be generated and stored outside the source
checkout. Only the public verification key belongs in the desktop application.

Issue a license with:

```powershell
dotnet run --project Ticketfy.Keygen -- `
  D:\SecureKeys\ticketfy-private.pem `
  D:\Licenses\customer.jwt `
  30 `
  customer-id
```

License tokens are validated against issuer `Ticketfy POS`, audience
`Ticketfy Client`, their signature and their expiration time.

Any signing key or deployment password that has previously appeared in Git
history must be revoked and rotated. Removing it from the current files does
not make the old credential safe again.

## Release artifacts

Build output belongs under `Ticketfy.Desktop/Output/Releases` and is published
locally during the maintenance window. Executables, ZIP files, Velopack packages,
payloads and version-specific manifests are ignored by the source repository.

`build_release.ps1` does not access the download site, timestamp service or
Forgejo in local development mode. Remote deployment scripts fail before opening
a connection.

`Ticketfy.Desktop.csproj` is the source of truth for the application version.
The release scripts read it automatically when no explicit version is supplied.
