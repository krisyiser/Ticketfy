using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ticketfy.Core.Models;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels;

/// <summary>
/// Drives the guided tutorial overlay.
/// Supports exact control pixel targeting via TargetName or normalized fallback fractions.
/// </summary>
public partial class TutorialOverlayViewModel : ObservableObject
{
    private readonly ITutorialService _tutorialService;
    private readonly string _stepKey;
    private List<TutorialStep> _steps = new();

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentTitle = string.Empty;
    [ObservableProperty] private string _currentDescription = string.Empty;
    [ObservableProperty] private string _stepLabel = string.Empty;
    [ObservableProperty] private string _nextButtonText = "SIGUIENTE";
    [ObservableProperty] private int _totalSteps;

    // Spotlight geometry — absolute px
    [ObservableProperty] private double _spotlightLeft;
    [ObservableProperty] private double _spotlightTop;
    [ObservableProperty] private double _spotlightWidth = 80;
    [ObservableProperty] private double _spotlightHeight = 80;

    // Coach tooltip position
    [ObservableProperty] private double _tooltipLeft;
    [ObservableProperty] private double _tooltipTop;

    // Arrow
    [ObservableProperty] private double _arrowLeft;
    [ObservableProperty] private double _arrowTop;
    [ObservableProperty] private double _arrowRotation;

    /// <summary>Fires when step changes so View can re-measure target control bounds.</summary>
    public event Action? StepChanged;

    /// <summary>Fires when all steps complete or user skips.</summary>
    public event Action? TutorialCompleted;

    public TutorialStep? CurrentStep => CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

    private double _panelWidth = 1280;
    private double _panelHeight = 800;

    private const double TooltipWidth = 300;
    private const double TooltipHeight = 160;
    private const double TooltipMargin = 16;

    public TutorialOverlayViewModel(ITutorialService tutorialService, string stepKey)
    {
        _tutorialService = tutorialService;
        _stepKey = stepKey;
    }

    /// <summary>
    /// Loads the step list. If the step key is already marked as done, exits silently.
    /// </summary>
    public async Task TryStartAsync(List<TutorialStep> steps)
    {
        if (await _tutorialService.IsStepCompletedAsync(_stepKey))
            return;

        _steps = steps;
        TotalSteps = steps.Count;
        CurrentIndex = 0;
        IsVisible = true;
        ApplyStep(0);
    }

    /// <summary>Called by the view's SizeChanged handler so coordinates stay accurate.</summary>
    public void UpdatePanelSize(double width, double height)
    {
        _panelWidth = width > 0 ? width : _panelWidth;
        _panelHeight = height > 0 ? height : _panelHeight;
    }

    private void ApplyStep(int index)
    {
        if (index < 0 || index >= _steps.Count) return;
        var step = _steps[index];

        CurrentTitle = step.Title;
        CurrentDescription = step.Description;
        StepLabel = $"Paso {index + 1} de {TotalSteps}";
        NextButtonText = (index == TotalSteps - 1) ? "FINALIZAR" : "SIGUIENTE";

        StepChanged?.Invoke();
    }

    /// <summary>
    /// Called by the View when target control is located and measured on screen.
    /// Sets exact pixel bounds and computes tooltip & arrow placement.
    /// </summary>
    public void SetSpotlightRect(double left, double top, double width, double height, TutorialAnchorSide side)
    {
        SpotlightLeft = left;
        SpotlightTop = top;
        SpotlightWidth = width;
        SpotlightHeight = height;

        double cx = left + width / 2.0;
        double cy = top + height / 2.0;

        double tLeft, tTop;
        double arLeft, arTop, arRot;

        switch (side)
        {
            case TutorialAnchorSide.Right:
                tLeft = left + width + TooltipMargin;
                tTop = cy - TooltipHeight / 2.0;
                arLeft = left + width;
                arTop = cy - 8;
                arRot = 0;
                break;
            case TutorialAnchorSide.Left:
                tLeft = left - TooltipWidth - TooltipMargin;
                tTop = cy - TooltipHeight / 2.0;
                arLeft = left - TooltipMargin;
                arTop = cy - 8;
                arRot = 180;
                break;
            case TutorialAnchorSide.Bottom:
                tLeft = cx - TooltipWidth / 2.0;
                tTop = top + height + TooltipMargin;
                arLeft = cx - 7;
                arTop = top + height;
                arRot = 90;
                break;
            default: // Top
                tLeft = cx - TooltipWidth / 2.0;
                tTop = top - TooltipHeight - TooltipMargin;
                arLeft = cx - 7;
                arTop = top - TooltipMargin;
                arRot = 270;
                break;
        }

        TooltipLeft = Math.Clamp(tLeft, 8, Math.Max(8, _panelWidth - TooltipWidth - 8));
        TooltipTop = Math.Clamp(tTop, 8, Math.Max(8, _panelHeight - TooltipHeight - 8));
        ArrowLeft = arLeft;
        ArrowTop = arTop;
        ArrowRotation = arRot;
    }

    /// <summary>
    /// Fallback positioning if a target control is not found in the Visual Tree.
    /// </summary>
    public void ApplyFallbackStep()
    {
        var step = CurrentStep;
        if (step == null) return;

        double cx = step.AnchorX * _panelWidth;
        double cy = step.AnchorY * _panelHeight;
        double left = cx - step.SpotlightWidth / 2.0;
        double top = cy - step.SpotlightHeight / 2.0;

        SetSpotlightRect(left, top, step.SpotlightWidth, step.SpotlightHeight, step.AnchorSide);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        int next = CurrentIndex + 1;
        if (next >= _steps.Count)
        {
            await CompleteTourAsync();
        }
        else
        {
            CurrentIndex = next;
            ApplyStep(next);
        }
    }

    [RelayCommand]
    private async Task SkipAllAsync()
    {
        await _tutorialService.MarkAllCompletedAsync();
        IsVisible = false;
        TutorialCompleted?.Invoke();
    }

    private async Task CompleteTourAsync()
    {
        await _tutorialService.MarkStepCompletedAsync(_stepKey);
        IsVisible = false;
        TutorialCompleted?.Invoke();
    }
}
