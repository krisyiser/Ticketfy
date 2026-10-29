using Avalonia;
using Avalonia.Controls;
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
