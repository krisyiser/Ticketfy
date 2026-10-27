using System.Net.Mail;

namespace Ticketfy.Services.Interfaces;

public enum InitialSetupStage { NotStarted, Business, Staff, Completed, ExistingInstallation }

public sealed record InitialBusinessProfile(string Name, string Phone, string Email, string Address)
{
    internal InitialBusinessProfile Validated()
    {
        static string Text(string? value, int maximum)
        {
            string text = (value ?? "").Trim();
            if (text.Length > maximum || text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                throw new InvalidOperationException("Los datos del negocio contienen texto demasiado largo o caracteres no admitidos.");
            return text;
        }
        var profile = new InitialBusinessProfile(Text(Name, 256), Text(Phone, 10), Text(Email, 254), Text(Address, 512));
        if (profile.Name.Length == 0) throw new InvalidOperationException("Indique el nombre del negocio.");
        if (profile.Phone.Length != 10 || !profile.Phone.All(char.IsAsciiDigit))
            throw new InvalidOperationException("El teléfono debe contener exactamente 10 dígitos.");
        if (profile.Email.Length > 0 && (!MailAddress.TryCreate(profile.Email, out var mail) || mail.Address != profile.Email))
            throw new InvalidOperationException("Revise el correo electrónico del negocio.");
        return profile;
    }
}
