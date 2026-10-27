using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Ticketfy.ViewModels.Navigation;

/// <summary>
/// Manages the single active dialog overlay.
/// Provides a clean interface so ViewModels can request dialogs
/// without knowing about MainWindow state.
/// </summary>
public partial class DialogCoordinator : ObservableObject
{
    [ObservableProperty] private ObservableObject? _activeDialogViewModel = null;
    [ObservableProperty] private bool _isDialogOverlayOpen = false;

    /// <summary>Shows a dialog by setting it as the active overlay.</summary>
    public void ShowDialog(ObservableObject viewModel)
    {
        var previous = ActiveDialogViewModel;
        ActiveDialogViewModel = viewModel;
        IsDialogOverlayOpen = true;
        if (!ReferenceEquals(previous, viewModel) && previous is IDismissibleDialog sensitive) sensitive.OnDismissed();
    }

    /// <summary>Clears the active dialog and closes the overlay.</summary>
    public void CloseDialog()
    {
        var previous = ActiveDialogViewModel;
        ActiveDialogViewModel = null;
        IsDialogOverlayOpen = false;
        if (previous is IDismissibleDialog sensitive) sensitive.OnDismissed();
    }

    public bool IsCurrent(ObservableObject? viewModel) => viewModel is not null
        && IsDialogOverlayOpen && ReferenceEquals(ActiveDialogViewModel, viewModel);

    /// <summary>A delayed callback can close only the dialog that originated it.</summary>
    public bool TryCloseDialog(ObservableObject? viewModel)
    {
        if (!IsCurrent(viewModel)) return false;
        CloseDialog();
        return true;
    }

    /// <summary>Convenience: shows dialog and auto-closes when RequestClose fires.</summary>
    public void ShowDialog(ObservableObject viewModel, Action? onClose)
    {
        ShowDialog(viewModel);
    }
}
