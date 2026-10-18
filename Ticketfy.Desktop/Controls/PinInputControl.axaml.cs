using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace Ticketfy.Controls;

public partial class PinInputControl : UserControl
{
    public static readonly DirectProperty<PinInputControl, string> PinValueProperty =
        AvaloniaProperty.RegisterDirect<PinInputControl, string>(
            nameof(PinValue),
            o => o.PinValue,
            (o, v) => o.PinValue = v);

    public static readonly StyledProperty<string> Pin1Property =
        AvaloniaProperty.Register<PinInputControl, string>(nameof(Pin1), defaultValue: string.Empty);

    public static readonly StyledProperty<string> Pin2Property =
        AvaloniaProperty.Register<PinInputControl, string>(nameof(Pin2), defaultValue: string.Empty);

    public static readonly StyledProperty<string> Pin3Property =
        AvaloniaProperty.Register<PinInputControl, string>(nameof(Pin3), defaultValue: string.Empty);

    public static readonly StyledProperty<string> Pin4Property =
        AvaloniaProperty.Register<PinInputControl, string>(nameof(Pin4), defaultValue: string.Empty);

    public string Pin1
    {
        get => GetValue(Pin1Property);
        set => SetValue(Pin1Property, value);
    }

    public string Pin2
    {
        get => GetValue(Pin2Property);
        set => SetValue(Pin2Property, value);
    }

    public string Pin3
    {
        get => GetValue(Pin3Property);
        set => SetValue(Pin3Property, value);
    }

    public string Pin4
    {
        get => GetValue(Pin4Property);
        set => SetValue(Pin4Property, value);
    }

    private string _pinValue = string.Empty;
    public string PinValue
    {
        get => _pinValue;
        set
        {
            if (SetAndRaise(PinValueProperty, ref _pinValue, value))
            {
                if (MasterInput.Text != value)
                {
                    MasterInput.Text = value ?? string.Empty;
                }
                UpdateVisualState(value ?? string.Empty);
            }
        }
    }

    public PinInputControl()
    {
        InitializeComponent();

        GotFocus += (s, e) => FocusMasterInput();
        PointerPressed += (s, e) => FocusMasterInput();

        MasterInput.PropertyChanged += (s, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                var text = MasterInput.Text ?? string.Empty;
                if (text.Length > 4)
                {
                    text = text.Substring(0, 4);
                    MasterInput.Text = text;
                }
                PinValue = text;
            }
        };

        MasterInput.GotFocus += (s, e) => UpdateVisualState(PinValue);
        MasterInput.LostFocus += (s, e) => UpdateVisualState(PinValue);
    }

    private void FocusMasterInput()
    {
        Dispatcher.UIThread.Post(() =>
        {
            MasterInput.Focus();
            if (MasterInput.Text != null)
            {
                MasterInput.CaretIndex = MasterInput.Text.Length;
            }
        });
    }

    private bool _isUpdatingVisualState = false;

    private void UpdateVisualState(string val)
    {
        val ??= string.Empty;

        _isUpdatingVisualState = true;
        try
        {
            Pin1 = val.Length > 0 ? val[0].ToString() : string.Empty;
            Pin2 = val.Length > 1 ? val[1].ToString() : string.Empty;
            Pin3 = val.Length > 2 ? val[2].ToString() : string.Empty;
            Pin4 = val.Length > 3 ? val[3].ToString() : string.Empty;
        }
        finally
        {
            _isUpdatingVisualState = false;
        }

        Dot1.Text = val.Length > 0 ? "●" : string.Empty;
        Dot2.Text = val.Length > 1 ? "●" : string.Empty;
        Dot3.Text = val.Length > 2 ? "●" : string.Empty;
        Dot4.Text = val.Length > 3 ? "●" : string.Empty;

        bool hasFocus = MasterInput?.IsFocused ?? false;

        // Rules for active highlight box:
        // 0 chars (empty) => Box 1 (index 0)
        // 1 char  => Box 1 (index 0)
        // 2 chars => Box 2 (index 1)
        // 3 chars => Box 3 (index 2)
        // 4 chars => Box 4 (index 3)
        int activeIndex = val.Length == 0 ? 0 : val.Length - 1;

        if (Box1 != null) HighlightBox(Box1, hasFocus && activeIndex == 0, val.Length > 0);
        if (Box2 != null) HighlightBox(Box2, hasFocus && activeIndex == 1, val.Length > 1);
        if (Box3 != null) HighlightBox(Box3, hasFocus && activeIndex == 2, val.Length > 2);
        if (Box4 != null) HighlightBox(Box4, hasFocus && activeIndex == 3, val.Length > 3);
    }

    private void HighlightBox(Border box, bool isActive, bool hasValue)
    {
        if (isActive)
        {
            box.BorderBrush = this.FindResource("AccentPrimaryBrush") as IBrush ?? Brushes.DodgerBlue;
            box.BorderThickness = new Thickness(2);
        }
        else if (hasValue)
        {
            box.BorderBrush = this.FindResource("BorderBrush") as IBrush ?? Brushes.Gray;
            box.BorderThickness = new Thickness(1.5);
        }
        else
        {
            box.BorderBrush = this.FindResource("BorderBrush") as IBrush ?? Brushes.LightGray;
            box.BorderThickness = new Thickness(1.5);
        }
    }

    public void ClearPin()
    {
        _isUpdatingVisualState = true;
        try
        {
            Pin1 = string.Empty;
            Pin2 = string.Empty;
            Pin3 = string.Empty;
            Pin4 = string.Empty;
        }
        finally
        {
            _isUpdatingVisualState = false;
        }
        PinValue = string.Empty;
        if (MasterInput != null)
        {
            MasterInput.Text = string.Empty;
        }
        UpdateVisualState(string.Empty);
        FocusMasterInput();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_isUpdatingVisualState) return;

        if (change.Property == Pin1Property || change.Property == Pin2Property || change.Property == Pin3Property || change.Property == Pin4Property)
        {
            if (string.IsNullOrEmpty(Pin1) || (string.IsNullOrEmpty(Pin1) && string.IsNullOrEmpty(Pin2) && string.IsNullOrEmpty(Pin3) && string.IsNullOrEmpty(Pin4)))
            {
                if (PinValue != string.Empty)
                {
                    PinValue = string.Empty;
                }
                else
                {
                    UpdateVisualState(string.Empty);
                }
            }
            else
            {
                string combined = $"{Pin1}{Pin2}{Pin3}{Pin4}";
                if (PinValue != combined)
                {
                    PinValue = combined;
                }
            }
        }
    }
}

