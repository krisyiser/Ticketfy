using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Ticketfy.Controls;

/// <summary>Do not submit a previous bound value when a field currently contains invalid text.</summary>
internal static class FormValidation
{
    internal static void GuardCommit(Control form, Button button)
    {
        button.AddHandler(Button.ClickEvent, (_, e) =>
        {
            var invalid = form.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.IsEffectivelyVisible && DataValidationErrors.GetHasErrors(c));
            if (invalid is null) return;
            e.Handled = true;
            invalid.BringIntoView();
            invalid.Focus();
        }, RoutingStrategies.Bubble);
    }
}
