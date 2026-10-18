using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Ticketfy.Core.Configuration;
using Ticketfy.Core.Helpers;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Security;

public enum LicenseState { Licensed, Evaluation, EvaluationExpired, Expired, Invalid, DeviceMismatch, ClockRollback }
public sealed record LicenseEvaluation(LicenseState State, bool IsAllowed, DateTimeOffset? ExpiresAt, int DaysRemaining, string Reason);

public sealed class LicenseEnforcementService : ILicenseEnforcementService
{
    internal const int EvaluationDays = 30;
    private const string Issuer = "Ticketfy POS";
    private const string Audience = "Ticketfy Client";
    private readonly string _licensePath;
    private readonly string _statePath;
    private readonly string _publicKeyPem;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _deviceId;
    private readonly bool _protectState;

    private const string ProductionPublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAoIKi6mxiZGlBgfSXIAYT
OphSxvgOYb3vsjPGILOTy27NnLvj88yjiPJwvtzY58kJN4w4y5JrY8xv0TbZfyZC
ey7VJLMLOUOVI1LHpcVqogOBgZ4IDwUxgf44zrKQ2mv1iFXjpT33WInm3SPzYdmy
ewiVHPIJ7pFeLAc0CX1rOKOqDayxWGS1TEMGP2MBtyT2STtSsMbERizUHhLH6/+M
PzhcYv8yWzIB4JaE7bYBtw/Zag227gltC4EFF/VP1Y32ayy1Q31WEKO6Eq6h/nK7
y/3t0VYVuqYzWnQU5Mcy8Qn4NPd7B/gcyNtCIYSSfMdS/qBk7kAMdLB2GULdzNAg
iwIDAQAB
-----END PUBLIC KEY-----";

    public LicenseEnforcementService() : this(AppDataPaths.LicenseFile, AppDataPaths.LicenseStateFile,
        ProductionPublicKey, () => DateTimeOffset.UtcNow, HardwareIdentityHelper.GetMotherboardUUID(), true)
    { }

    internal LicenseEnforcementService(string licensePath, string statePath, string publicKeyPem,
        Func<DateTimeOffset> clock, string deviceId, bool protectState)
    {
        _licensePath = licensePath;
        _statePath = statePath;
        _publicKeyPem = publicKeyPem;
        _clock = clock;
        _deviceId = deviceId;
        _protectState = protectState;
    }

    public bool IsSystemLocked() => !Evaluate().IsAllowed;

    public LicenseEvaluation Evaluate()
    {
        DateTimeOffset now = _clock();
        LicenseClockState state;
        try { state = LoadOrCreateState(now); }
        catch { return new(LicenseState.Invalid, false, null, 0, "No fue posible validar el estado seguro de licencia."); }

        if (now < state.LastSeenUtc.AddMinutes(-5))
            return new(LicenseState.ClockRollback, false, null, 0, "Se detectó retroceso del reloj del sistema.");

        state.LastSeenUtc = now;
        SaveState(state);

        if (!File.Exists(_licensePath))
        {
            DateTimeOffset expires = state.TrialStartedUtc.AddDays(EvaluationDays);
            int days = Math.Max(0, (int)Math.Ceiling((expires - now).TotalDays));
            return now <= expires
                ? new(LicenseState.Evaluation, true, expires, days, "Evaluación local activa.")
                : new(LicenseState.EvaluationExpired, false, expires, 0, "El periodo de evaluación terminó.");
        }

        try
        {
            ClaimsPrincipal principal = ValidateToken(File.ReadAllText(_licensePath).Trim(), now, out var token);
            string? licensedDevice = principal.FindFirst("device_id")?.Value;
            if (string.IsNullOrWhiteSpace(licensedDevice) || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(licensedDevice), System.Text.Encoding.UTF8.GetBytes(_deviceId)))
                return new(LicenseState.DeviceMismatch, false, token.ValidTo, 0, "La licencia no corresponde a este dispositivo.");

            var expires = new DateTimeOffset(token.ValidTo, TimeSpan.Zero);
            return new(LicenseState.Licensed, true, expires,
                Math.Max(0, (int)Math.Ceiling((expires - now).TotalDays)), "Licencia firmada válida en modo sin conexión.");
        }
        catch (SecurityTokenExpiredException ex)
        {
            return new(LicenseState.Expired, false, ex.Expires, 0, "La licencia caducó.");
        }
        catch
        {
            return new(LicenseState.Invalid, false, null, 0, "La licencia no es válida.");
        }
    }

    public void InstallRenewal(string token)
    {
        ClaimsPrincipal principal = ValidateToken(token.Trim(), _clock(), out _);
        string? licensedDevice = principal.FindFirst("device_id")?.Value;
        if (string.IsNullOrWhiteSpace(licensedDevice) || !string.Equals(licensedDevice, _deviceId, StringComparison.Ordinal))
            throw new SecurityTokenValidationException("La renovación no corresponde a este dispositivo.");
        string directory = Path.GetDirectoryName(_licensePath) ?? throw new InvalidOperationException("Ruta de licencia inválida.");
        Directory.CreateDirectory(directory);
        string temporary = _licensePath + ".tmp";
        File.WriteAllText(temporary, token.Trim());
        File.Move(temporary, _licensePath, true);
    }

    private ClaimsPrincipal ValidateToken(string tokenString, DateTimeOffset now, out JwtSecurityToken token)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_publicKeyPem);
        var handler = new JwtSecurityTokenHandler();
        var signingKey = new RsaSecurityKey(rsa)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateLifetime = false,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero
        };
        var principal = handler.ValidateToken(tokenString, parameters, out var validated);
        token = (JwtSecurityToken)validated;
        if (token.ValidFrom > now.UtcDateTime) throw new SecurityTokenNotYetValidException();
        if (token.ValidTo <= now.UtcDateTime)
            throw new SecurityTokenExpiredException("La licencia caducó.") { Expires = token.ValidTo };
        return principal;
    }

    private LicenseClockState LoadOrCreateState(DateTimeOffset now)
    {
        string? json = null;
        if (File.Exists(_statePath))
            json = _protectState ? new DpapiSecretStore(_statePath).Read() : File.ReadAllText(_statePath);
        var state = json == null ? null : JsonSerializer.Deserialize<LicenseClockState>(json);
        if (state != null) return state;
        state = new LicenseClockState { TrialStartedUtc = now, LastSeenUtc = now };
        SaveState(state);
        return state;
    }

    private void SaveState(LicenseClockState state)
    {
        string json = JsonSerializer.Serialize(state);
        if (_protectState) new DpapiSecretStore(_statePath).Write(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, json);
        }
    }

    private sealed class LicenseClockState
    {
        public DateTimeOffset TrialStartedUtc { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; }
    }
}
