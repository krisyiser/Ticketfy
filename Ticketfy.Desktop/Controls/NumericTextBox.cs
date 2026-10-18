using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Text.RegularExpressions;

namespace Ticketfy.Controls;

/// <summary>
/// Industrial numeric text box that strictly blocks letters and non-numeric characters,
/// including clipboard paste attempts, IME entries, and invalid key combinations.
/// Supports optional decimal places and maximum digit length.
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

    private bool _isSanitizing;

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

        string pattern = AllowDecimals ? "^[0-9.]+$" : "^[0-9]+$";

        if (!Regex.IsMatch(e.Text, pattern))
        {
            e.Handled = true;
        }
        else if (AllowDecimals && e.Text == "." && Text != null && Text.Contains("."))
        {
            // Block multiple decimal dots
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_isSanitizing)
        {
            var newText = change.GetNewValue<string>();
            if (!string.IsNullOrEmpty(newText))
            {
                var sanitized = SanitizeInput(newText);
                if (sanitized != newText)
                {
                    _isSanitizing = true;
                    Text = sanitized;
                    CaretIndex = sanitized.Length;
                    _isSanitizing = false;
                }
            }
        }
    }

    private string SanitizeInput(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return string.Empty;

        var result = new System.Text.StringBuilder();
        bool hasDecimal = false;

        foreach (char c in rawText)
        {
            if (char.IsDigit(c))
            {
                if (MaxDigits <= 0 || result.Length < MaxDigits)
                {
                    result.Append(c);
                }
            }
            else if (AllowDecimals && c == '.' && !hasDecimal)
            {
                if (MaxDigits <= 0 || result.Length < MaxDigits)
                {
                    result.Append(c);
                    hasDecimal = true;
                }
            }
        }

        return result.ToString();
    }
}
