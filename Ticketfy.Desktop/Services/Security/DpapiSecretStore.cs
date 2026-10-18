using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ticketfy.Services.Security;

/// <summary>
/// Persists application secrets encrypted for the current Windows user through
/// DPAPI. A copied file cannot be decrypted by another user or workstation.
/// </summary>
internal sealed class DpapiSecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("Ticketfy.Security.v2");

    private readonly string _path;

    internal DpapiSecretStore(string path) => _path = path;

    internal bool Exists => File.Exists(_path);

    internal void Write(string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("El almacén seguro requiere Windows DPAPI.");
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("El secreto no puede estar vacío.", nameof(secret));

        byte[] plaintext = Encoding.UTF8.GetBytes(secret);
        byte[] protectedBytes;
        try { protectedBytes = Protect(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("La ruta del almacén seguro no es válida.");
        Directory.CreateDirectory(directory);

        string temporaryPath = _path + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    internal string Read()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("El almacén seguro requiere Windows DPAPI.");
        if (!File.Exists(_path))
            throw new FileNotFoundException("No existe el secreto protegido.", _path);

        byte[] plaintext = Unprotect(File.ReadAllBytes(_path));
        try
        {
            string value = Encoding.UTF8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("El secreto protegido está vacío.");
            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Protect(byte[] plaintext) => InvokeDpapi(plaintext, protect: true);
    private static byte[] Unprotect(byte[] ciphertext) => InvokeDpapi(ciphertext, protect: false);

    private static byte[] InvokeDpapi(byte[] input, bool protect)
    {
        DATA_BLOB inputBlob = CreateBlob(input);
        DATA_BLOB entropyBlob = CreateBlob(OptionalEntropy);
        DATA_BLOB outputBlob = default;
        try
        {
            bool success = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI no pudo procesar el secreto.");

            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, outputBlob.cbData);
            return result;
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
            if (outputBlob.pbData != IntPtr.Zero) LocalFree(outputBlob.pbData);
        }
    }

    private static DATA_BLOB CreateBlob(byte[] bytes)
    {
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DATA_BLOB { cbData = bytes.Length, pbData = pointer };
    }

    private static void FreeBlob(DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.pbData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
