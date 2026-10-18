using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Constants;
using Ticketfy.Services.Interfaces;
using Serilog;

namespace Ticketfy.Services.Implementations;

public class PrintDispatcherService : IPrintDispatcherService
{
    private readonly IEscPosPrinterService _thermalPrinter;
    private readonly ICertificateGeneratorService _certificateGenerator;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsService _settings;

    public PrintDispatcherService(
        IEscPosPrinterService thermalPrinter,
        ICertificateGeneratorService certificateGenerator,
        IDbContextFactory<AppDbContext> dbFactory,
        ISettingsService settings)
    {
        _thermalPrinter = thermalPrinter;
        _certificateGenerator = certificateGenerator;
        _dbFactory = dbFactory;
        _settings = settings;
    }

    public async Task DispatchSaleDocumentsAsync(SaleDto sale, bool printReceipt = true)
    {
        // 1. Imprimir y esperar confirmación del controlador; encolar no equivale
        // a haber escrito correctamente en el dispositivo.
        var appSettings = await _settings.GetAppSettingsAsync();
        string printerTarget = string.IsNullOrWhiteSpace(appSettings.Hardware.PrinterPort)
            ? "ImpresoraTickets"
            : appSettings.Hardware.PrinterPort.Trim();
        if (sale.CashAmount > 0m && appSettings.Hardware.EnableCashDrawerTrigger)
        {
            bool drawerOpened = await _thermalPrinter.OpenCashDrawerAsync(printerTarget, appSettings.Hardware.CashDrawerPin);
            if (!drawerOpened)
                Log.Error("Cash drawer pulse for sale {SaleId} failed on {PrinterTarget}", sale.Id, printerTarget);
        }
        if (printReceipt)
        {
            bool ticketPrinted = await _thermalPrinter.PrintTicketAsync(sale, printerTarget);
            if (!ticketPrinted)
                Log.Error("Receipt {SaleId} could not be written to configured printer {PrinterTarget}", sale.Id, printerTarget);
        }

        // 2. Analizar el carrito en busca de items Premium
        // Load attributes from DB for the products in the sale
        using var ctx = await _dbFactory.CreateDbContextAsync();
        var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();

        var premiumProducts = await ctx.Products
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync();

        var premiumAttributes = await ctx.ProductAttributes
            .Where(a => productIds.Contains(a.ProductId) && a.AttributeName == PrintRoutingTags.CertificateOfAuthenticity)
            .ToListAsync();

        var premiumItems = sale.Items.Where(i =>
            premiumAttributes.Any(a => a.ProductId == i.ProductId)
        ).ToList();

        if (!premiumItems.Any()) return; // Venta normal, terminar aquí.

        string premiumPrinterName = await _settings.GetAsync("PremiumPrinterName") ?? "Microsoft Print to PDF";

        // 3. Enrutar trabajos de alta calidad
        foreach (var item in premiumItems)
        {
            try
            {
                var product = premiumProducts.FirstOrDefault(p => p.Id == item.ProductId);
                if (product != null)
                {
                    byte[] pdfBytes = _certificateGenerator.GenerateCertificateOfAuthenticity(sale, product);
                    await SendToPremiumPrinterAsync(pdfBytes, premiumPrinterName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Fallo al imprimir certificado para {item.Name}");
            }
        }
    }

    private async Task SendToPremiumPrinterAsync(byte[] pdfBytes, string printerName)
    {
        // Guardar temporalmente en RAM o Temp Path para dárselo al OS Spooler
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        await File.WriteAllBytesAsync(tempFile, pdfBytes);

        // Enviar silenciosamente al spooler de Windows hacia la impresora láser/inyección
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = "PrintTo",
            Arguments = $"\"{printerName}\"",
            FileName = tempFile,
            UseShellExecute = true
        };

        process.Start();
        // Disparar y olvidar. El OS maneja la cola de impresión láser.
    }
}
