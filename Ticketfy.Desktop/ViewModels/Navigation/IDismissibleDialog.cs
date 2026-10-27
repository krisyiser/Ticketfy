namespace Ticketfy.ViewModels.Navigation;

/// <summary>Invalidates in-flight decisions when an overlay is closed or replaced.</summary>
public interface IDismissibleDialog
{
    void OnDismissed();
}
