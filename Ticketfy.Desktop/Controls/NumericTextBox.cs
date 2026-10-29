using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Text.RegularExpressions;

namespace Ticketfy.Controls;

/// <summary>
/// Numeric typing assistance. Pasted text is retained for validation, never rewritten into another amount.
/// Supports decimal separators and a typing length limit.
/// </summary>
public class NumericTextBox : TextBox
{
    private bool _refreshingValidation;
    protected override Type StyleKeyOverride => typeof(TextBox);

    public static readonly StyledProperty<bool> AllowDecimalsProperty =
        AvaloniaProperty.Register<NumericTextBox, bool>(nameof(AllowDecimals), defaultValue: false);

    public static readonly StyledProperty<int> MaxDigitsProperty =
        AvaloniaProperty.Register<NumericTextBox, int>(nameof(MaxDigits), defaultValue: 0);

    public bool AllowDecimals
    {
        get => GetValue(AllowDecimalsProperty);
        set => SetValue(AllowDecimalsProperty, value);
    }

    public int MaxDigits
    {
        get => GetValue(MaxDigitsProperty);
        set => SetValue(MaxDigitsProperty, value);
    }

    public NumericTextBox()
    {
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        GotFocus += OnGotFocus;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty || _refreshingValidation || !DataValidationErrors.GetHasErrors(this)) return;
        var entered = Text;
        if (!string.IsNullOrWhiteSpace(entered) && !Ticketfy.Core.Helpers.NumericEntry.TryDecimal(entered, out _)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_refreshingValidation || Text != entered || !DataValidationErrors.GetHasErrors(this)) return;
            var binding = BindingOperations.GetBindingExpressionBase(this, TextProperty);
            if (binding is null) return;
            _refreshingValidation = true;
            try
            {
                // Avalonia 11.2 skips an unchanged source write, leaving a previous conversion error.
                // Re-read validation, then restore and revalidate the exact input. Never replace an
                // invalid amount with the previous source value or suppress a setter's rejection.
                binding.UpdateTarget();
                SetCurrentValue(TextProperty, entered);
                binding.UpdateSource();
            }
            finally { _refreshingValidation = false; }
        });
    }

    private void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        Dispatcher.UIThread.Post(SelectAll);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;

        // Check max length
        if (MaxDigits > 0 && Text != null && Text.Length >= MaxDigits && SelectionStart == SelectionEnd)
        {
            e.Handled = true;
            return;
        }

        string pattern = AllowDecimals ? "^[0-9.,]+$" : "^[0-9]+$";

        if (!Regex.IsMatch(e.Text, pattern))
        {
            e.Handled = true;
        }
        else if (AllowDecimals && e.Text is "." or "," && Text != null && Text.IndexOfAny(['.', ',']) >= 0 && SelectionStart == SelectionEnd)
        {
            // Block multiple decimal dots
            e.Handled = true;
        }
    }

    // Paste is validated by the binding. Never silently turn "-50", "1e3" or "5oops0" into a different amount.
}
