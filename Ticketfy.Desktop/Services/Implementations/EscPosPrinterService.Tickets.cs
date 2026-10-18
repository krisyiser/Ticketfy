using System;
using System.IO;
using System.Threading.Tasks;
using Ticketfy.Data.Dtos;
using Ticketfy.Core.Models;
using Serilog;

namespace Ticketfy.Services.Implementations;

/// <summary>
/// Partial class extension of EscPosPrinterService for formatting POS sale tickets,
/// cash movement slips, and ESC/POS test receipt pages.
/// </summary>
public partial class EscPosPrinterService
{
    public Task<bool> PrintTicketAsync(SaleDto sale, string printerPortOrName = "COM1")
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // Code Page 19 (CP858)

            // Header
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "TICKETFY!\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);

            WriteString(ms, "SUCURSAL MATRIZ - CENTRO HISTÓRICO\n");
            WriteString(ms, "RFC: XAXX010101000 | TEL: 55-5000-0000\n");
            WriteString(ms, $"FOLIO TICKET: #{sale.Id.Substring(0, Math.Min(8, sale.Id.Length)).ToUpper()}\n");
            WriteString(ms, $"FECHA: {sale.LocalDateDisplay}\n");
            WriteString(ms, "================================================\n");

            // Items Table
            ms.Write(AlignLeft, 0, AlignLeft.Length);
            foreach (var item in sale.Items)
            {
                var qtyStr = item.Unit.Equals("pza", StringComparison.OrdinalIgnoreCase) ? $"{Math.Round(item.Quantity)}" : $"{item.Quantity:N2}{item.Unit}";
                var line = $"{qtyStr} x {item.Name}\n";
                WriteString(ms, line);
                ms.Write(AlignRight, 0, AlignRight.Length);
                WriteString(ms, $"${item.TotalPrice:N2}\n");
                ms.Write(AlignLeft, 0, AlignLeft.Length);
            }

            // Totals
            WriteString(ms, "------------------------------------------------\n");
            ms.Write(AlignRight, 0, AlignRight.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, $"TOTAL: ${sale.Total:N2}\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, $"PAGO EFECTIVO: ${sale.PaidAmount:N2}\n");
            WriteString(ms, $"CAMBIO ENTREGADO: ${sale.ChangeAmount:N2}\n");

            if (!string.IsNullOrEmpty(sale.EstadoFiscal) && sale.EstadoFiscal.Contains("CFDI"))
            {
                WriteString(ms, "------------------------------------------------\n");
                ms.Write(AlignCenter, 0, AlignCenter.Length);
                WriteString(ms, "FACTURA ELECTRÓNICA CFDI 4.0 TIMBRADA\n");
                WriteString(ms, "ESTADO: APROBADO SAT\n");
            }

            // Native QR Code Generator (ESC/POS GS ( k)
            WriteString(ms, "\n");
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            WriteEscPosQrCode(ms, $"https://verificacfdi.facturaelectronica.sat.gob.mx/default.aspx?id={sale.Id}");

            // Footer
            WriteString(ms, "\n¡Gracias por su preferencia!\nConserve este comprobante para cualquier aclaración.\n\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            byte[] rawData = ms.ToArray();
            return EnqueuePrintJobAsync(printerPortOrName, rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing ESC/POS ticket payload");
            return Task.FromResult(false);
        }
    }

    public Task<bool> PrintNonSaleCashMovementSlipAsync(ShiftMovementSlipModel model, string printerPortOrName = "COM1")
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // CP858

            // Header
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "COMPROBANTE DE CAJA\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);

            WriteString(ms, "================================================\n");
            ms.Write(AlignLeft, 0, AlignLeft.Length);
            WriteString(ms, $"TIPO: {model.MovementTypeLabel.ToUpper()}\n");
            WriteString(ms, $"FOLIO: {model.Folio}\n");
            WriteString(ms, $"FECHA: {model.Timestamp:dd/MM/yyyy HH:mm}\n");
            WriteString(ms, $"CAJERO: {model.CashierName}\n");
            WriteString(ms, "------------------------------------------------\n");
            WriteString(ms, "CONCEPTO:\n");
            WriteString(ms, $"{model.Description}\n");
            WriteString(ms, "------------------------------------------------\n");
            ms.Write(AlignRight, 0, AlignRight.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, $"MONTO: ${model.Amount:N2}\n");
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, "================================================\n");
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            WriteString(ms, "\n\n________________________________\n");
            WriteString(ms, "FIRMA DE RECIBIDO\n\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            byte[] rawData = ms.ToArray();
            return EnqueuePrintJobAsync(printerPortOrName, rawData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing non-sale cash movement slip payload");
            return Task.FromResult(false);
        }
    }

    public Task<bool> PrintTestPageAsync(string printerPortOrName = "COM1")
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(EscInit, 0, EscInit.Length);
            ms.Write(new byte[] { 0x1B, 0x74, 0x13 }, 0, 3); // CP858
            ms.Write(AlignCenter, 0, AlignCenter.Length);
            ms.Write(DoubleSizeOn, 0, DoubleSizeOn.Length);
            ms.Write(BoldOn, 0, BoldOn.Length);
            WriteString(ms, "TICKETFY!\n");
            ms.Write(DoubleSizeOff, 0, DoubleSizeOff.Length);
            ms.Write(BoldOff, 0, BoldOff.Length);
            WriteString(ms, "PRUEBA DE IMPRESIÓN TÉRMICA ESC/POS\n");
            WriteString(ms, "Estado: COM / USB Driver Conectado OK\n");
            WriteString(ms, $"Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");

            WriteEscPosQrCode(ms, "https://ticketfy.pos/test");
            WriteString(ms, "\n\n");
            ms.Write(PaperCut, 0, PaperCut.Length);

            return EnqueuePrintJobAsync(printerPortOrName, ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error preparing ESC/POS test page payload");
            return Task.FromResult(false);
        }
    }
}
