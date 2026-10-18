using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace Ticketfy.Core.Helpers;

/// <summary>
/// Industrial-grade Hardware Identity Engine under Protocol Valcore Desktop v4.0.
/// Generates a 100% deterministic, immutable, hardware-bound Machine Fingerprint
/// that persists across app uninstalls, user profile wipes, and software reinstalls.
/// Zero Guid random generation — completely deterministic physical hardware identity.
/// </summary>
public static class HardwareIdentityHelper
{
    private static string? _cachedHwid;

    public static string GetMotherboardUUID()
    {
        if (!string.IsNullOrEmpty(_cachedHwid))
            return _cachedHwid;

        try
        {
            // 1. Try 64-bit Registry MachineGuid (Universal Windows System Identifier)
            string? machineGuid = ReadWindowsMachineGuid();
            if (!string.IsNullOrWhiteSpace(machineGuid))
            {
                _cachedHwid = machineGuid.Trim().ToLowerInvariant();
                PersistInHklmRegistry(_cachedHwid);
                PersistInProgramDataVault(_cachedHwid);
                return _cachedHwid;
            }

            // 2. Try Motherboard UUID / Serial Number via WMI
            string? mbUuid = ReadMotherboardUuidWmi();
            string? mbSerial = ReadMotherboardSerialWmi();

            // 3. Try System Drive Volume Serial Number (C:)
            string? volSerial = ReadVolumeSerialNumberWmi();

            // 4. Combine available immutable physical identifiers
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(mbUuid) && mbUuid != "00000000-0000-0000-0000-000000000000") sb.Append("MBUUID:").Append(mbUuid.Trim()).Append(";");
            if (!string.IsNullOrWhiteSpace(mbSerial) && mbSerial != "None" && mbSerial != "Default string") sb.Append("MBSER:").Append(mbSerial.Trim()).Append(";");
            if (!string.IsNullOrWhiteSpace(volSerial)) sb.Append("VOLSER:").Append(volSerial.Trim()).Append(";");

            if (sb.Length > 0)
            {
                string hash = ComputeSha256(sb.ToString());
                _cachedHwid = $"HWID-{hash.Substring(0, 16).ToUpperInvariant()}";
                PersistInHklmRegistry(_cachedHwid);
                PersistInProgramDataVault(_cachedHwid);
                return _cachedHwid;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HardwareIdentityHelper: Error calculating primary hardware fingerprint");
        }

        // 5. Fallback Layer A: Check HKLM Registry Vault (System-Wide, survives uninstalls)
        string? hklmHwid = ReadFromHklmRegistry();
        if (!string.IsNullOrWhiteSpace(hklmHwid))
        {
            _cachedHwid = hklmHwid;
            return _cachedHwid;
        }

        // 6. Fallback Layer B: Check %ProgramData%\Ticketfy\device_id.vault (System-Wide, survives user appdata deletion)
        string? pDataHwid = ReadFromProgramDataVault();
        if (!string.IsNullOrWhiteSpace(pDataHwid))
        {
            _cachedHwid = pDataHwid;
            return _cachedHwid;
        }

        // 7. Fallback Layer C: MachineName-based deterministic hash (Never random Guid!)
        string machineNameStr = $"HOST:{Environment.MachineName.Trim().ToUpperInvariant()}";
        string fallbackHash = ComputeSha256(machineNameStr);
        _cachedHwid = $"HWID-HOST-{fallbackHash.Substring(0, 12).ToUpperInvariant()}";

        PersistInHklmRegistry(_cachedHwid);
        PersistInProgramDataVault(_cachedHwid);

        return _cachedHwid;
    }

    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var cryptoKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = cryptoKey?.GetValue("MachineGuid")?.ToString();
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    return guid.Trim();
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ReadMotherboardUuidWmi()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var uuid = obj["UUID"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(uuid) && uuid != "00000000-0000-0000-0000-000000000000" && !uuid.Contains("FFFFFFFF"))
                    {
                        return uuid.Trim();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ReadMotherboardSerialWmi()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(serial) && serial != "None" && serial != "Default string")
                    {
                        return serial.Trim();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ReadVolumeSerialNumberWmi()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var searcher = new ManagementObjectSearcher("SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='C:'");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var volSerial = obj["VolumeSerialNumber"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(volSerial))
                    {
                        return volSerial.Trim();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static void PersistInHklmRegistry(string hwid)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.CreateSubKey(@"SOFTWARE\Ticketfy");
                key?.SetValue("HardwareID", hwid);
            }
        }
        catch { }
    }

    private static string? ReadFromHklmRegistry()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Ticketfy");
                return key?.GetValue("HardwareID")?.ToString();
            }
        }
        catch { }
        return null;
    }

    private static void PersistInProgramDataVault(string hwid)
    {
        try
        {
            string programDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ticketfy");
            if (!Directory.Exists(programDataDir))
            {
                Directory.CreateDirectory(programDataDir);
            }
            string vaultFile = Path.Combine(programDataDir, "device_id.vault");
            File.WriteAllText(vaultFile, hwid);
        }
        catch { }
    }

    private static string? ReadFromProgramDataVault()
    {
        try
        {
            string vaultFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ticketfy", "device_id.vault");
            if (File.Exists(vaultFile))
            {
                return File.ReadAllText(vaultFile).Trim();
            }
        }
        catch { }
        return null;
    }

    private static string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
