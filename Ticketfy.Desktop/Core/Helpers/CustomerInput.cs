using System.Net.Mail;
using Ticketfy.Data.Dtos;

namespace Ticketfy.Core.Helpers;

public static class CustomerInput
{
    public static CustomerDto Normalize(CustomerDto customer)
    {
        var name = (customer.Name ?? "").Trim();
        var phone = (customer.Phone ?? "").Trim();
        var email = (customer.Email ?? "").Trim();
        var code = (customer.CustomerCode ?? "").Trim();
        var rfc = (customer.Rfc ?? "").Trim();
        if (name.Length is 0 or > 200 || name.Any(char.IsControl))
            throw new InvalidOperationException("Indique el nombre del cliente, con hasta 200 caracteres y sin saltos de línea.");
        if (phone.Length > 32 || phone.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '(' or ')' or '-' or '+')))
            throw new InvalidOperationException("Revise el teléfono; utilice 10 dígitos o deje el campo vacío.");
        phone = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (phone.Length is not (0 or 10))
            throw new InvalidOperationException("El teléfono debe contener 10 dígitos o quedar vacío.");
        if (email.Length > 254 || email.Any(char.IsControl) || (email.Length > 0
            && (!MailAddress.TryCreate(email, out var address) || address.Address != email || !address.Host.Contains('.'))))
            throw new InvalidOperationException("Revise el correo electrónico o deje el campo vacío.");
        if (code.Length > 128 || code.Any(char.IsControl) || rfc.Length > 32 || rfc.Any(char.IsControl))
            throw new InvalidOperationException("Revise el código del cliente y el RFC; no deben contener controles ni saltos de línea.");
        if (!NumericEntry.IsMoney(customer.CreditLimit))
            throw new InvalidOperationException("El límite de crédito debe ser no negativo y tener hasta dos decimales.");
        return customer with { Nombre = name, Telefono = phone, Email = email, CustomerCode = code, Rfc = rfc };
    }
}
