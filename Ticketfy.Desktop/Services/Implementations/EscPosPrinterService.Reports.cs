using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;
using Serilog;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Partial class extension of EscPosPrinterService for formatting inventory checklists,
/// stock snapshots, and purchase order tickets.
/// </summary>
public partial class EscPosPrinterService
{
    public Task<bool> PrintInventoryChecklistAsync(List<ProductDto> products, string printerPortOrName = "")
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // CP858

            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "INVENTARIO - CHECKLIST\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, $"FECHA: {Ticketfy.Core.Helpers.BusinessTime.Now:dd/MM/yyyy HH:mm}\n");
            WriteString(ms, "================================================\n");

            ms.Write(AlignLeft, 0, AlignLeft.Length);
            WriteString(ms, "CODIGO     DESC.               STOCK   FISICO\n");
            WriteString(ms, "------------------------------------------------\n");

            foreach (var p in products)
            {
                string cod = (p.Barcode ?? p.Id).Length > 10 ? (p.Barcode ?? p.Id).Substring(0, 10) : (p.Barcode ?? p.Id).PadRight(10);
                string desc = p.Name.Length > 18 ? p.Name.Substring(0, 18) : p.Name.PadRight(18);
                string stock = p.Stock.ToString("0.###").PadLeft(7);

                WriteString(ms, $"{cod} {desc} {stock}  [    ]\n");
            }

            WriteString(ms, "------------------------------------------------\n");
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            WriteString(ms, "\n\n________________________________\n");
            WriteString(ms, "FIRMA DE SUPERVISOR\n\n\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            byte[] rawData = ms.ToArray();
            return EnqueuePrintJobAsync(printerPortOrName, rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing inventory checklist payload");
            return Task.FromResult(false);
        }
    }

    public Task<bool> PrintSnapshotChecklistAsync(Ticketfy.Data.Entities.InventorySnapshotEntity snapshot, string printerPortOrName = "")
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // CP858

            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "REPORTE DE INVENTARIO\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, $"FECHA: {snapshot.CreatedAt:dd/MM/yyyy HH:mm}\n");
            if (!string.IsNullOrEmpty(snapshot.Notes))
                WriteString(ms, $"NOTAS: {snapshot.Notes}\n");
            WriteString(ms, "================================================\n");

            ms.Write(AlignLeft, 0, AlignLeft.Length);
            WriteString(ms, "CODIGO     DESC.               STOCK   FISICO\n");
            WriteString(ms, "------------------------------------------------\n");

            foreach (var p in snapshot.Items)
            {
                string cod = (p.Barcode ?? p.ProductId).Length > 10 ? (p.Barcode ?? p.ProductId).Substring(0, 10) : (p.Barcode ?? p.ProductId).PadRight(10);
                string desc = p.Name.Length > 18 ? p.Name.Substring(0, 18) : p.Name.PadRight(18);
                string stock = p.Quantity.ToString("0.###").PadLeft(7);

                WriteString(ms, $"{cod} {desc} {stock}  [    ]\n");
            }

            WriteString(ms, "------------------------------------------------\n");
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            WriteString(ms, $"ARTICULOS EN ESTA IMPRESION: {snapshot.Items.Count}\n");
            WriteString(ms, $"ARTICULOS DE TODA LA CAPTURA: {snapshot.TotalItems}\n");
            WriteString(ms, $"VALOR DE TODA LA CAPTURA: {snapshot.TotalValue:C}\n");
            WriteString(ms, "\n\n________________________________\n");
            WriteString(ms, "FIRMA DE SUPERVISOR\n\n\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            byte[] rawData = ms.ToArray();
            return EnqueuePrintJobAsync(printerPortOrName, rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing snapshot checklist payload");
            return Task.FromResult(false);
        }
    }

    public Task<bool> PrintPurchaseOrderAsync(PurchaseDto purchase, string printerPortOrName = "")
    {
        if (!purchase.DetailsAvailable) return Task.FromResult(false);
        try
        {
            PurchaseDocumentReader.ValidateDetail(purchase);
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // CP858

            // Header
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "TICKETFY!\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, "REMISION / ENTRADA DE MERCANCIA\n");
            WriteString(ms, "================================================\n");

            ms.Write(AlignLeft, 0, AlignLeft.Length);
            WriteString(ms, $"FOLIO: {purchase.InvoiceNumber}\n");
            WriteString(ms, $"PROVEEDOR: {purchase.SupplierName}\n");
            if (DateTime.TryParse(purchase.Date, out var dt))
                WriteString(ms, $"FECHA: {dt:dd/MM/yyyy HH:mm}\n");
            else
                WriteString(ms, $"FECHA: {purchase.Date}\n");
            WriteString(ms, "------------------------------------------------\n");

            // Items
            WriteString(ms, "DESC.                   CANT    COSTO\n");
            WriteString(ms, "------------------------------------------------\n");
            if (purchase.Items != null)
            {
                foreach (var item in purchase.Items)
                {
                    string desc = item.ProductName.Length > 22 ? item.ProductName.Substring(0, 22) : item.ProductName.PadRight(22);
                    string qty = item.Quantity.ToString("N3").PadLeft(7);
                    string cost = $"${item.TotalPrice:N2}".PadLeft(8);
                    WriteString(ms, $"{desc} {qty} {cost}\n");
                }
            }

            WriteString(ms, "------------------------------------------------\n");
            ms.Write(AlignRight, 0, AlignRight.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            WriteString(ms, $"TOTAL: ${purchase.TotalCost:N2}\n");
            WriteString(ms, $"PAGOS DOCUMENTADOS: {purchase.RecordedPaymentsDisplay}\n");
            WriteString(ms, $"SALDO (+DEUDA/-CREDITO): {purchase.BalanceDisplay}\n");
            WriteString(ms, purchase.FinancialStatus + "\n");
            WriteString(ms, "No sustituye comprobantes bancarios.\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);

            ms.Write(AlignCenter, 0, AlignCenter.Length);
            WriteString(ms, "\n\n________________________________\n");
            WriteString(ms, "FIRMA DE RECIBIDO\n\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            byte[] rawData = ms.ToArray();
            return EnqueuePrintJobAsync(printerPortOrName, rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing purchase order receipt payload");
            return Task.FromResult(false);
        }
    }
}
