using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ticketfy.Core.Helpers;

/// <summary>Technical details belong in the sanitized log, never in an operator's form.</summary>
public static class UserFacingErrors
{
    public static Func<object, object> ValidationConverter { get; } = _ =>
        "Revise este campo. Introduzca un valor válido; los importes y cantidades deben ser números.";

    public static string Message(Exception exception, string fallback = "No se pudo completar la operación. Revise los datos e inténtelo de nuevo.")
    {
        if (exception is DbUpdateConcurrencyException)
            return "Otro movimiento actualizó estos datos. Vuelva a abrir el registro antes de guardar.";
        if (exception.GetBaseException() is SqliteException database)
            return database.SqliteErrorCode switch
            {
                5 or 6 => "La base está ocupada. Espere un momento y vuelva a intentar la operación.",
                13 => "No hay espacio suficiente para guardar. Libere espacio en el equipo antes de continuar.",
                19 => "Ya existe un registro con esos datos o falta un dato relacionado. Revise el formulario.",
                _ => "No se pudo acceder a los datos. Consulte el diagnóstico del sistema."
            };
        // Only messages explicitly raised by our own application are operator instructions.
        // Framework/driver exceptions (including InvalidOperationException) are not safe UI text.
        if (exception is InvalidOperationException
            && exception.TargetSite?.DeclaringType?.Assembly == typeof(UserFacingErrors).Assembly
            && exception.InnerException is null)
            return exception.Message;
        if (exception is UnauthorizedAccessException)
            return "Su sesión no tiene permiso para esta operación. Solicite apoyo al encargado.";
        return fallback;
    }
}
