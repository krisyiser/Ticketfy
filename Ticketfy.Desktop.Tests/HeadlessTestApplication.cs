using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Ticketfy.Desktop.Tests.TestAppBuilder))]

namespace Ticketfy.Desktop.Tests;

public sealed class HeadlessTestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources["BgPrimaryBrush"] = Brushes.White;
        Resources["BgSecondaryBrush"] = Brushes.White;
        Resources["BgTertiaryBrush"] = Brushes.WhiteSmoke;
        Resources["BorderBrush"] = Brushes.Gray;
        Resources["TextPrimaryBrush"] = Brushes.Black;
        Resources["TextMutedBrush"] = Brushes.DimGray;
        Resources["AccentPrimaryBrush"] = Brushes.DodgerBlue;
        Resources["AccentDangerBrush"] = Brushes.Crimson;
        Resources["AccentSuccessBrush"] = Brushes.ForestGreen;
        Resources["AppCornerRadius"] = new CornerRadius(6);
        Resources["AppFontFamily"] = FontFamily.Default;
        Resources["PosCartWidth"] = 380d;
        Resources["PosPriceFontSize"] = 24d;
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
