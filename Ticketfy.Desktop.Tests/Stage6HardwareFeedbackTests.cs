using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Controls;
using Ticketfy.Core.Messages;
using Ticketfy.Core.Models;
using Ticketfy.Core.Models.Settings;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;
using Ticketfy.ViewModels.Shell;

namespace Ticketfy.Desktop.Tests;

public sealed partial class Stage6CommercialLedgerTests
{
    private sealed class HardwareSettings : ISettingsService
    {
        public AppSettings Value { get; } = new();
        public Task<AppSettings> GetAppSettingsAsync() => Task.FromResult(Value);
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => Task.FromResult(new Dictionary<string, string>());
        public Task SaveAppSettingsAsync(AppSettings settings) => throw new NotSupportedException();
    }
    private sealed class FakeHardwareInventory : IHardwareInventoryReader
    {
        public HardwareInventory Inventory { get; set; } = new([], [], []);
        public bool Fail { get; set; }
        public Task<HardwareInventory> ReadAsync() => Fail ? Task.FromException<HardwareInventory>(new IOException()) : Task.FromResult(Inventory);
    }
    private sealed class ReceiptPrinter(bool succeeds) : IEscPosPrinterService
    {
        public int Tickets { get; private set; }
        public int Drawers { get; private set; }
        public string? Target { get; private set; }
        public Task<bool> PrintTicketAsync(SaleDto sale, string printerPortOrName = "")
        { Tickets++; Target = printerPortOrName; return Task.FromResult(succeeds); }
        public Task<bool> OpenCashDrawerAsync(string printerPortOrName = "", int drawerPin = 0)
        { Drawers++; Target = printerPortOrName; return Task.FromResult(succeeds); }
        public Task<bool> PrintNonSaleCashMovementSlipAsync(ShiftMovementSlipModel model, string printerPortOrName = "") => Task.FromResult(succeeds);
        public Task<bool> PrintTestPageAsync(string printerPortOrName = "") => Task.FromResult(succeeds);
        public Task<bool> IsPrinterOnlineAsync() => Task.FromResult(false);
        public Task<bool> PrintInventoryChecklistAsync(List<ProductDto> products, string printerPortOrName = "") => Task.FromResult(succeeds);
        public Task<bool> PrintSnapshotChecklistAsync(InventorySnapshotEntity snapshot, string printerPortOrName = "") => Task.FromResult(succeeds);
        public Task<bool> PrintPurchaseOrderAsync(PurchaseDto purchase, string printerPortOrName = "") => Task.FromResult(succeeds);
    }

    [Theory]
    [InlineData("", "Sin configurar", true)]
    [InlineData("Printer X", "Cola detectada", false)]
    [InlineData("Missing", "Cola no detectada", true)]
    [InlineData("COM7@9600", "Puerto detectado", false)]
    [InlineData("COM8", "Puerto no detectado", true)]
    [InlineData("tcp://192.0.2.1:9100", "Configurada; sin comprobar", true)]
    public void HardwareDetection_DoesNotConfuseConfigurationWithPhysicalVerification(string target, string expected, bool attention)
    {
        var inventory = new HardwareInventory([new("Printer X", false)], ["COM7"], ["HID Keyboard"]);
        var states = HardwareStatusService.Evaluate(new() { PrinterPort = target, ScalePort = "COM7", EnableCashDrawerTrigger = true }, inventory);
        Assert.Equal(expected, states[0].State);
        Assert.Equal(attention, states[0].NeedsAttention);
        Assert.Equal("Configurado; sin sensor", states[1].State);
        Assert.Equal("Pendiente de prueba de entrada", states[2].State);
        Assert.Equal("Puerto detectado", states[3].State);
    }

    [Fact]
    public void HardwareDetection_ReportsOfflineQueueAndIncompleteInventory()
    {
        var states = HardwareStatusService.Evaluate(new() { PrinterPort = "Offline" }, new([new("Offline", true)], [], [], "Consulta parcial"));
        Assert.Equal("Cola no disponible", states[0].State);
        Assert.True(states[0].NeedsAttention);
        Assert.Equal("Consulta incompleta", states.Last().State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptDispatch_ReportsDriverResultWithoutClaimingPhysicalPrinting(bool succeeds)
    {
        var settings = new HardwareSettings();
        settings.Value.Hardware.PrinterPort = "Configured Printer";
        settings.Value.Hardware.EnableCashDrawerTrigger = true;
        var printer = new ReceiptPrinter(succeeds);
        var dispatcher = new PrintDispatcherService(printer, new CertificateGeneratorService(), new TestDbContextFactory(_options), settings);
        HardwareFeedbackMessage? outcome = null;
        var receiver = new object();
        WeakReferenceMessenger.Default.Register<HardwareFeedbackMessage>(receiver, (_, message) => outcome = message);
        try
        {
            await dispatcher.DispatchSaleDocumentsAsync(Sale("PRINT"));
            Assert.NotNull(outcome);
            Assert.Equal(!succeeds, outcome.IsError);
            Assert.Contains("Venta PRINT registrada", outcome.Text);
            Assert.Contains(succeeds ? "Compruebe la salida física" : "No se confirmó", outcome.Text);
            Assert.Equal(1, printer.Tickets); Assert.Equal(1, printer.Drawers);
            Assert.Equal("Configured Printer", printer.Target);
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(receiver); }
    }

    [Fact]
    public async Task UnconfiguredPrinting_FailsVisiblyWithoutFallbackOrPhysicalIO()
    {
        var settings = new HardwareSettings();
        var printer = new ReceiptPrinter(true);
        var dispatcher = new PrintDispatcherService(printer, new CertificateGeneratorService(), new TestDbContextFactory(_options), settings);
        HardwareFeedbackMessage? outcome = null;
        var receiver = new object();
        WeakReferenceMessenger.Default.Register<HardwareFeedbackMessage>(receiver, (_, message) => outcome = message);
        try
        {
            await dispatcher.DispatchSaleDocumentsAsync(Sale("NO-PRINTER"));
            Assert.True(outcome!.IsError); Assert.Equal(0, printer.Tickets); Assert.Equal(0, printer.Drawers);
            await dispatcher.DispatchSaleDocumentsAsync(Sale("NO-PRINT"), false);
            Assert.False(outcome!.IsError); Assert.Contains("no solicitada", outcome.Text);
            await using var driver = new EscPosPrinterService(settings);
            Assert.False(await driver.PrintTestPageAsync());
            Assert.False(await driver.IsPrinterOnlineAsync());
        }
        finally { WeakReferenceMessenger.Default.UnregisterAll(receiver); }
    }

    [AvaloniaFact]
    public async Task HardwarePanel_ReportsChangedDetectionAndPrintFailureWithoutStoringScannedCode()
    {
        var settings = new HardwareSettings(); settings.Value.Hardware.PrinterPort = "Connected";
        var reader = new FakeHardwareInventory();
        using var vm = new HardwareStatusViewModel(settings, reader);
        var view = new HardwareStatusControl { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 450 }; window.Show();
        try
        {
            vm.ToggleDetailsCommand.Execute(null);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Cola no detectada", vm.Devices[0].State);
            reader.Inventory = new([new("Connected", false)], [], ["Keyboard"]);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("Cola detectada", vm.Devices[0].State);
            Assert.Contains("Cambio", vm.LastChange);
            vm.ScannerInput = "PRIVATE-BARCODE";
            vm.ConfirmScannerInputCommand.Execute(null);
            Assert.Contains("Entrada recibida", vm.ScannerResult);
            Assert.Equal("", vm.ScannerInput); Assert.DoesNotContain("PRIVATE-BARCODE", vm.ScannerResult);
            HardwareFeedbackMessage.Report("Venta registrada; impresora desconectada. No vuelva a cobrar.", true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(vm.IsPrintError); Assert.Contains("No vuelva a cobrar", vm.LastPrintMessage);
            reader.Fail = true; await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("No disponible", Assert.Single(vm.Devices).State);
            Assert.Contains("No vuelva a cobrar", vm.LastPrintMessage);
        }
        finally { window.Close(); }
    }
}
