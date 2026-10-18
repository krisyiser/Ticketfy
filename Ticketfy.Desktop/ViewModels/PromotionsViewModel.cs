using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Ticketfy.ViewModels;

public partial class PromotionsViewModel : ObservableObject
{
    private readonly IPromotionService _promotionService;
    private readonly IItemKitService? _kitService;
    public ObservableCollection<PromotionDto> Promotions { get; } = [];

    [ObservableProperty] private string _feedbackMessage = string.Empty;
    [ObservableProperty] private bool _isFeedbackError = false;

    public event Action? OpenCreateItemKitRequested;
    public event Action? OpenCreatePromotionRequested;
    public event Action<PromotionDto>? OpenEditPromotionRequested;

    public PromotionsViewModel(IPromotionService promotionService, IItemKitService? kitService = null)
    {
        _promotionService = promotionService;
        _kitService = kitService;
        _ = LoadPromotionsAsync();
    }

    public async Task LoadPromotionsAsync()
    {
        try
        {
            var items = await _promotionService.GetAllAsync();
            var promosList = items.ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Promotions.Clear();
                foreach (var item in promosList) Promotions.Add(item);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error loading promotions");
        }
    }

    [RelayCommand]
    private void OpenAddPromotionDialog() => OpenCreatePromotionRequested?.Invoke();

    [RelayCommand]
    private void EditPromotion(PromotionDto promotion) => OpenEditPromotionRequested?.Invoke(promotion);

    [RelayCommand]
    private void OpenCreateItemKitDialog() => OpenCreateItemKitRequested?.Invoke();

    [RelayCommand]
    private async Task ToggleStatusAsync(PromotionDto promo)
    {
        if (promo == null) return;
        try
        {
            var updated = promo with { IsActive = !promo.IsActive };
            await _promotionService.SaveAsync(updated);
            FeedbackMessage = promo.IsActive ? "Promoción desactivada" : "Promoción activada";
            await LoadPromotionsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error toggling promotion status");
            IsFeedbackError = true; FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeletePromotionAsync(PromotionDto promo)
    {
        if (promo == null) return;
        try
        {
            await _promotionService.DeleteAsync(promo.Id);
            Promotions.Remove(promo);
            FeedbackMessage = "Promoción inactivada; se conserva el historial";
            await LoadPromotionsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting promotion");
            IsFeedbackError = true; FeedbackMessage = ex.Message;
        }
    }
}
