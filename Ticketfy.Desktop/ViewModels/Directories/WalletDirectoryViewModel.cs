using CommunityToolkit.Mvvm.Input;
using Ticketfy.Data.Dtos;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.ViewModels.Directories;

public partial class WalletDirectoryViewModel(IGiftcardService service, Action<string> select)
    : PagedDirectoryViewModel<WalletDirectoryRow>
{
    protected override async Task<DirectoryRead<WalletDirectoryRow>> ReadAsync(int page, string search, bool inactive)
    {
        var result = await service.GetPageAsync(page, 100, search, inactive);
        return new(result.Items, result.HasMore,
            $"{result.TotalCount:N0} monederos en el filtro · Saldo registrado total: {result.TotalRecordedBalance:C2}. "
            + "Incluye todas las páginas. Consulte el estado de cuenta para comprobar el respaldo en movimientos."
            + (inactive ? " Incluye inactivos." : " Sólo activos."));
    }
    [RelayCommand]
    private void UseSelected()
    {
        if (OwnsRow(SelectedItem)) select(SelectedItem!.CardNumber);
    }
}
