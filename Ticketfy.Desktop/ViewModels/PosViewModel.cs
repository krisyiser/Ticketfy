using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Core.State;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Ticketfy.Data.Dtos;
using System;
using System.Linq;
using System.Threading.Tasks;
using Ticketfy.Services;
using Ticketfy.ViewModels.Dialogs;

namespace Ticketfy.ViewModels;

public partial class PosViewModel : ObservableObject, IDisposable
{
    public CatalogViewModel Catalog { get; }
    public CartViewModel Cart { get; }
    public PosHeaderViewModel Header { get; }

    private readonly CartStateStore _cartStateStore;
    private readonly IPredictiveIntelligenceService? _predictiveService;
    private readonly ISessionManager? _sessionManager;
    private int _sessionVersion;
    private long _suggestionVersion;
    private CancellationTokenSource? _suggestionCancellation;
    private readonly TimeProvider _suggestionClock;
    private bool _disposed;
    internal Task SuggestionCompletion { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private int _cartGridColumn = 1;
    [ObservableProperty] private int _productsGridColumn = 0;
    [ObservableProperty] private string _posGridColumnDefinitions = "*,Auto";
    [ObservableProperty] private Thickness _productsMargin = new(0, 0, 16, 0);
    [ObservableProperty] private double _cartWidthPx = 380;

    [ObservableProperty] private string _smartSuggestionText = string.Empty;
    [ObservableProperty] private bool _isSuggestionVisible;
    private ProductDto? _suggestedProduct;

    public event Action? OpenCheckoutRequested;
    public event Action? OpenAddCustomerRequested;
    public event Action? ToggleFullscreenRequested;
    public event Action? LogoutRequested;
    public event Action? OpenShiftNotesRequested;
    public event Action? OpenSwitchUserPinRequested;
    public event Action? OpenLockScreenRequested;
    public event Action<string, Action<bool>>? OpenSupervisorPinRequested;
    public event Action? OpenPartialCashupRequested;
    public event Action? OpenFinalCashupRequested;

    public PosViewModel(
        IProductService productService,
        IShiftNoteService? shiftNoteService = null,
        ICustomerService? customerService = null,
        ISessionManager? sessionManager = null,
        IPromotionService? promotionService = null,
        IPredictiveIntelligenceService? predictiveService = null,
        IExternalCatalogService? externalCatalogService = null,
        TimeProvider? suggestionClock = null,
        bool loadOnCreate = true)
    {
        _predictiveService = predictiveService;
        _sessionManager = sessionManager;
        _suggestionClock = suggestionClock ?? TimeProvider.System;
        _cartStateStore = new CartStateStore { PromotionService = promotionService };

        Catalog = new CatalogViewModel(productService, externalCatalogService!, _cartStateStore);
        Cart = new CartViewModel(_cartStateStore, null!, customerService!, loadOnCreate, sessionManager);
        Header = new PosHeaderViewModel(sessionManager, shiftNoteService, _cartStateStore, loadOnCreate);
        _loadOnSessionChange = loadOnCreate;
        if (_sessionManager is not null)
        {
            _sessionManager.CashierChanged += OnCashierChanged;
            _sessionManager.LockStateChanged += OnLockStateChanged;
        }

        _cartStateStore.ProductAddedToCart += OnProductAddedToCart;
        _cartStateStore.Items.CollectionChanged += OnCartItemsChanged;

        Cart.OpenCheckoutRequested += () => OpenCheckoutRequested?.Invoke();
        Cart.OpenAddCustomerRequested += () => OpenAddCustomerRequested?.Invoke();
        Header.ToggleFullscreenRequested += () => ToggleFullscreenRequested?.Invoke();
        Header.LogoutRequested += () => LogoutRequested?.Invoke();
        Header.OpenShiftNotesRequested += () => OpenShiftNotesRequested?.Invoke();
        Header.OpenSwitchUserPinRequested += () => OpenSwitchUserPinRequested?.Invoke();
        Header.OpenLockScreenRequested += () => OpenLockScreenRequested?.Invoke();
        Header.OpenSupervisorPinRequested += (title, cb) => OpenSupervisorPinRequested?.Invoke(title, cb);
        Header.OpenPartialCashupRequested += () => OpenPartialCashupRequested?.Invoke();
        Header.OpenFinalCashupRequested += () => OpenFinalCashupRequested?.Invoke();

        ThemeService.Instance.CartWidthChanged += OnCartWidthChanged;
        ThemeService.Instance.CartPositionChanged += OnCartPositionChanged;

        RegisterMessages();
    }

    private void OnLockStateChanged(bool locked) => Cart.InvalidateCheckoutPreparation();

    private void OnCashierChanged(Ticketfy.Core.Models.UserModel? user)
    {
        ++_sessionVersion;
        Catalog.ClearSessionData();
        Cart.ClearSessionData();
        InvalidateSuggestion();
        if (_loadOnSessionChange && user is not null && _sessionManager?.IsTerminalLocked == false)
        {
            _ = Catalog.LoadProductsAsync();
            _ = Cart.LoadCustomersAsync();
        }
    }

    public void ApplyCartPosition(string position)
    {
        if (position == "Izquierda")
        {
            CartGridColumn = 0;
            ProductsGridColumn = 1;
            PosGridColumnDefinitions = "Auto,*";
            ProductsMargin = new Thickness(16, 0, 0, 0);
        }
        else
        {
            CartGridColumn = 1;
            ProductsGridColumn = 0;
            PosGridColumnDefinitions = "*,Auto";
            ProductsMargin = new Thickness(0, 0, 16, 0);
        }
    }

    public void RegisterMessages()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        WeakReferenceMessenger.Default.Register<ForceLogoutMessage>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Cart.ClearCartCommand.Execute(null);
            });
        });
    }

    private void OnProductAddedToCart(string scannedProductId)
    {
        SuggestionCompletion = TriggerCrossSellSuggestionAsync(scannedProductId);
    }

    private void OnCartItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => InvalidateSuggestion();
    private void OnCartWidthChanged(double width) => Dispatcher.UIThread.Post(() => { if (!_disposed) CartWidthPx = width; });
    private void OnCartPositionChanged(string position) => Dispatcher.UIThread.Post(() => { if (!_disposed) ApplyCartPosition(position); });

    private void InvalidateSuggestion()
    {
        ++_suggestionVersion;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation = null;
        _suggestedProduct = null;
        IsSuggestionVisible = false; SmartSuggestionText = "";
    }

    private async Task TriggerCrossSellSuggestionAsync(string scannedProductId)
    {
        InvalidateSuggestion();
        if (_disposed || _predictiveService == null) return;
        long request = _suggestionVersion;
        var source = new CancellationTokenSource();
        _suggestionCancellation = source;
        var token = source.Token;
        var cartIds = _cartStateStore.Items.Select(i => i.ProductId).ToList();
        int version = _sessionVersion;
        try
        {
            // SQLite's async API can run synchronously; keep the database traversal off the UI thread.
            var suggestion = await Task.Run(() => _predictiveService.GetTopCorrelatedProductAsync(scannedProductId, cartIds, token), token);
            if (_disposed || token.IsCancellationRequested || version != _sessionVersion || request != _suggestionVersion) return;
            if (suggestion is null || !suggestion.IsActive || suggestion.Stock <= 0 ||
                suggestion.InventoryWarning is not null || _cartStateStore.Items.Any(i => i.ProductId == suggestion.Id)) return;
            _suggestedProduct = suggestion;
            SmartSuggestionText = $"Sugerencia: Ofrece {suggestion.Name} (${suggestion.Price}). ¡Se venden juntos a menudo!";
            IsSuggestionVisible = true;
            await Task.Delay(TimeSpan.FromSeconds(10), _suggestionClock, token);
            if (request == _suggestionVersion && !_disposed) InvalidateSuggestion();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Product suggestion lookup failed");
            if (request == _suggestionVersion && !_disposed) InvalidateSuggestion();
        }
        finally
        {
            if (ReferenceEquals(_suggestionCancellation, source)) _suggestionCancellation = null;
            source.Dispose();
        }
    }

    [RelayCommand]
    private void AddSuggestionToCart()
    {
        if (!_disposed && IsSuggestionVisible && _suggestedProduct is { } suggestion)
        {
            InvalidateSuggestion();
            if (!_cartStateStore.Items.Any(i => i.ProductId == suggestion.Id))
                Catalog.AddToCartCommand.Execute(suggestion);
        }
    }

    public async Task LoadProductsAsync() => await Catalog.LoadProductsAsync();
    private readonly bool _loadOnSessionChange;
    public async Task LoadSessionAsync()
    {
        var version = _sessionVersion;
        await Catalog.LoadProductsAsync();
        if (version != _sessionVersion) return;
        await Cart.LoadCustomersAsync();
        if (version != _sessionVersion) return;
        await Header.LoadActiveShiftNotesAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InvalidateSuggestion();
        _cartStateStore.ProductAddedToCart -= OnProductAddedToCart;
        _cartStateStore.Items.CollectionChanged -= OnCartItemsChanged;
        ThemeService.Instance.CartWidthChanged -= OnCartWidthChanged;
        ThemeService.Instance.CartPositionChanged -= OnCartPositionChanged;
        if (_sessionManager is not null)
        {
            _sessionManager.CashierChanged -= OnCashierChanged;
            _sessionManager.LockStateChanged -= OnLockStateChanged;
        }
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
