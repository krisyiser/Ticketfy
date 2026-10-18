using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Ticketfy.Keygen;

internal static class Program
{
    private const string Issuer = "Ticketfy POS";
    private const string Audience = "Ticketfy Client";

    private static int Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "Uso: Ticketfy.Keygen <private-key.pem> <output.jwt> <dias-vigencia> <cliente> <device-id> [tipo-licencia]");
            return 2;
        }

        string privateKeyPath = Path.GetFullPath(args[0]);
        string outputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(privateKeyPath))
        {
            Console.Error.WriteLine($"No se encontró la clave privada: {privateKeyPath}");
            return 2;
        }

        if (!int.TryParse(args.ElementAtOrDefault(2) ?? "30", out int validityDays)
            || validityDays is < 1 or > 366)
        {
            Console.Error.WriteLine("La vigencia debe estar entre 1 y 366 días.");
            return 2;
        }

        string customer = args.ElementAtOrDefault(3)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(customer))
        {
            Console.Error.WriteLine("Debe indicar un identificador de cliente.");
            return 2;
        }

        string deviceId = args.ElementAtOrDefault(4)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Console.Error.WriteLine("Debe indicar el identificador del dispositivo.");
            return 2;
        }
        string licenseType = args.ElementAtOrDefault(5)?.Trim() ?? "commercial";

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(privateKeyPath));

            var now = DateTime.UtcNow;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Subject = new ClaimsIdentity(
                [
                    new Claim(JwtRegisteredClaimNames.Sub, customer),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                    new Claim("device_id", deviceId),
                    new Claim("license_type", licenseType)
                ]),
                NotBefore = now,
                IssuedAt = now,
                Expires = now.AddDays(validityDays),
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(rsa),
                    SecurityAlgorithms.RsaSha256)
            };

            string token = new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, token);
            Console.WriteLine($"Licencia emitida para '{customer}' hasta {descriptor.Expires:O}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo emitir la licencia: {ex.Message}");
            return 1;
        }
    }
}
