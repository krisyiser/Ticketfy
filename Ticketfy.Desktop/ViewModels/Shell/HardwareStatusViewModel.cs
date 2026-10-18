using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Services.Implementations;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels.Shell;

public partial class HardwareStatusViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IHardwareInventoryReader _reader;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    public ObservableCollection<PeripheralStatus> Devices { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _summary = "Periféricos: sin consultar";
    [ObservableProperty] private string _lastChecked = "";
    [ObservableProperty] private string _lastChange = "";
    [ObservableProperty] private string _lastPrintMessage = "Todavía no se ha solicitado una impresión.";
    [ObservableProperty] private bool _isPrintError;
    [ObservableProperty] private string _scannerInput = "";
    [ObservableProperty] private string _scannerResult = "Escanee un código aquí y pulse Enter. Esta prueba no altera el inventario ni cobra.";

    public HardwareStatusViewModel(ISettingsService settings, IHardwareInventoryReader? reader = null)
    {
        _settings = settings;
        _reader = reader ?? new WindowsHardwareInventoryReader();
        _timer.Tick += OnTick;
        WeakReferenceMessenger.Default.Register<HardwareFeedbackMessage>(this, (recipient, message) =>
            Dispatcher.UIThread.Post(() =>
            {
                var vm = (HardwareStatusViewModel)recipient;
                vm.LastPrintMessage = message.Text;
                vm.IsPrintError = message.IsError;
            }));
    }

    public void Start() { if (_timer.IsEnabled) return; _timer.Start(); _ = RefreshAsync(); }
    private void OnTick(object? sender, EventArgs args) => _ = RefreshAsync();
    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; WeakReferenceMessenger.Default.UnregisterAll(this); }
    [RelayCommand] private void ToggleDetails() => IsExpanded = !IsExpanded;
    [RelayCommand]
    private void ConfirmScannerInput()
    {
        string input = ScannerInput.Trim();
        ScannerResult = input.Length is > 0 and <= 128 && !input.Any(char.IsControl)
            ? $"Entrada recibida ({input.Length} caracteres) a las {DateTime.Now:HH:mm:ss}. Confirme que procede del lector, no del teclado."
            : "No se recibió un código válido (1–128 caracteres sin controles).";
        ScannerInput = ""; // Never retain scanned identifiers in logs or the status panel.
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var config = await _settings.GetAppSettingsAsync();
            var inventory = await _reader.ReadAsync();
            var next = HardwareStatusService.Evaluate(config.Hardware, inventory);
            if (Devices.Count > 0 && !Devices.SequenceEqual(next)) LastChange = $"Cambio de configuración o detección a las {DateTime.Now:HH:mm:ss}. Revise los periféricos.";
            Devices.Clear(); foreach (var device in next) Devices.Add(device);
            Summary = $"Periféricos · {next.Count(d => d.NeedsAttention)} aviso(s) · ver detalle";
            LastChecked = $"Última consulta: {DateTime.Now:HH:mm:ss}. Actualización cada 30 s; no es una certificación de funcionamiento.";
        }
        catch
        {
            Devices.Clear();
            Devices.Add(new("Detección", "No disponible", "No se pudo leer la configuración o consultar Windows. Reintente; no se confirma ningún dispositivo.", true));
            Summary = "Periféricos · detección no disponible";
        }
        finally { IsRefreshing = false; }
    }
}
