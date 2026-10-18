using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Ticketfy.Core.Messages;
using Ticketfy.Core.State;
using Ticketfy.Services.Interfaces;
using Ticketfy.Core.Services;
using Ticketfy.Data;
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
        AppDbContext? db = null,
        IShiftNoteService? shiftNoteService = null,
        IItemKitService? kitService = null,
        ICustomerService? customerService = null,
        ISessionManager? sessionManager = null,
        Ticketfy.Core.Repositories.IUserRepository? userRepository = null,
        IPromotionService? promotionService = null,
        IAuditService? auditService = null,
        IAttendanceService? attendanceService = null,
        IPredictiveIntelligenceService? predictiveService = null,
        IExternalCatalogService? externalCatalogService = null)
    {
        _predictiveService = predictiveService;
        _cartStateStore = new CartStateStore();

        Catalog = new CatalogViewModel(productService, externalCatalogService!, kitService, _cartStateStore);
        Cart = new CartViewModel(_cartStateStore, null!, customerService!); // Ideally SaleService injected
        Header = new PosHeaderViewModel(sessionManager, shiftNoteService, _cartStateStore);

        _cartStateStore.ProductAddedToCart += OnProductAddedToCart;

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

        ThemeService.Instance.CartWidthChanged += width => Dispatcher.UIThread.Post(() => CartWidthPx = width);
        ThemeService.Instance.CartPositionChanged += pos => Dispatcher.UIThread.Post(() => ApplyCartPosition(pos));

        RegisterMessages();
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
        _ = TriggerCrossSellSuggestionAsync(scannedProductId);
    }

    private async Task TriggerCrossSellSuggestionAsync(string scannedProductId)
    {
        if (_predictiveService == null) return;

        var cartIds = _cartStateStore.Items.Select(i => i.ProductId).ToList();

        // Consulta no bloqueante
        var suggestion = await _predictiveService.GetTopCorrelatedProductAsync(scannedProductId, cartIds);

        if (suggestion != null)
        {
            _suggestedProduct = suggestion;
            SmartSuggestionText = $"Sugerencia: Ofrece {suggestion.Name} (${suggestion.Price}). ¡Se venden juntos a menudo!";
            IsSuggestionVisible = true;

            // Auto-ocultar la sugerencia después de 10 segundos
            await Task.Delay(10000);
            if (_suggestedProduct?.Id == suggestion.Id) // only hide if it hasn't changed
            {
                IsSuggestionVisible = false;
            }
        }
    }

    [RelayCommand]
    private void AddSuggestionToCart()
    {
        if (_suggestedProduct != null)
        {
            Catalog.AddToCartCommand.Execute(_suggestedProduct);
            IsSuggestionVisible = false;
            _suggestedProduct = null;
        }
    }

    public async Task LoadProductsAsync() => await Catalog.LoadProductsAsync();

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
