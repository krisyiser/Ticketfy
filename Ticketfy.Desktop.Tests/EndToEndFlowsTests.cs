using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Core.Enums;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Implementations;
using Xunit;

namespace Ticketfy.Desktop.Tests;

/// <summary>
/// Comprehensive End-to-End Business Flow Tests.
/// Validates concrete real-world operational cycles:
/// Supplier registration -> Product creation -> Simple Cash Sale -> Mixed Payment Sale -> Customer Credit & Payment -> Shift Closure Z-Cut.
/// </summary>
public sealed class EndToEndFlowsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly SpyAuditService _auditSpy;
    private readonly Ticketfy.Services.CommercialOperationContext _actor;
    private readonly SaleService _saleService;

    private AppDbContext CreateContext() => new AppDbContext(_options);
    private ProductService ProductService => new ProductService(CreateContext(), _actor);
    private SupplierService SupplierService => new SupplierService(CreateContext(), _actor);
    private CustomerService CustomerService => new CustomerService(CreateContext(), operations: _actor);
    private ShiftService ShiftService => new ShiftService(CreateContext(), _actor);

    public EndToEndFlowsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var initCtx = CreateContext())
        {
            initCtx.Database.EnsureCreated();
        }

        _actor = CommercialTestSession.Create(_options);
        _auditSpy = new SpyAuditService();
        _factory = new TestDbContextFactory(_options);
        _saleService = new SaleService(_factory, new CoOccurrenceQueue(), _auditSpy, _actor);
    }

    [Fact]
    public async Task Flow1_SupplierRegistrationAndRetrieval_ShouldPersistCorrectly()
    {
        // 1. Register new supplier
        var supplierDto = new SupplierDto(
            Id: "SUPP-VALCORE-01",
            Name: "Distribuidora Monterrey S.A. de C.V.",
            Rfc: "DMO900101ABC",
            Phone: "8181234567",
            Email: "ventas@distribuidoramonterrey.com",
            Address: "Av. Industrial 450, Monterrey, N.L.",
            ContactPerson: "Carlos Villarreal",
            IsActive: true
        );

        await SupplierService.CreateAsync(supplierDto);

        // 2. Verify supplier persists and can be queried
        var suppliers = await SupplierService.GetAllAsync();
        Assert.Contains(suppliers, s => s.Id == "SUPP-VALCORE-01");
        var created = suppliers.First(s => s.Id == "SUPP-VALCORE-01");

        Assert.Equal("SUPP-VALCORE-01", created.Id);
        Assert.Equal("Distribuidora Monterrey S.A. de C.V.", created.Name);
        Assert.Equal("DMO900101ABC", created.Rfc);
        Assert.Equal("Carlos Villarreal", created.ContactPerson);
    }

    [Fact]
    public async Task Flow2_NewProductEntryLinkedToSupplier_ShouldCalculateHeadroomAndStock()
    {
        // 1. Register supplier
        var supp = new SupplierDto("SUPP-02", "Bebidas del Norte", "BNO880101XYZ", "8180001122", "contacto@bebidas.com", "Calle 10", "Laura", true);
        await SupplierService.CreateAsync(supp);

        // 2. Add product linked to supplier
        var newProduct = new ProductDto(
            Id: "PROD-BEB-01",
            Barcode: "7501234567890",
            Name: "Refresco de Cola 600ml",
            Cost: 12.50,
            Price: 20.00,
            WholesalePrice: 17.00,
            WholesaleThreshold: 12,
            Stock: 50.0,
            Category: "Bebidas",
            Unit: "Pza",
            ExpiresSoon: 0,
            CreatedAt: DateTimeOffset.Now.ToString("o"),
            PointsRewarded: 2.0,
            ReorderQuantity: 24.0,
            LocationRack: "Pasillo 2 - Estante B",
            SatProductCode: "50202306",
            SatUnitCode: "H87",
            MinStock: 10.0,
            DefaultSupplierId: "SUPP-02",
            IsBulk: false,
            IsKit: false
        );

        await ProductService.AddAsync(newProduct);

        // 3. Verify Product catalog loading & details
        var fetchedProduct = await ProductService.GetByBarcodeAsync("7501234567890");
        Assert.NotNull(fetchedProduct);
        Assert.Equal("Refresco de Cola 600ml", fetchedProduct.Name);
        Assert.Equal(12.50m, fetchedProduct.Cost);
        Assert.Equal(20.00m, fetchedProduct.Price);
        Assert.Equal(50.0, fetchedProduct.Stock);
        Assert.Equal("SUPP-02", fetchedProduct.DefaultSupplierId);
        Assert.True(fetchedProduct.IsAvailable);
        Assert.False(fetchedProduct.IsOutOfStock);
    }

    [Fact]
    public async Task Flow3_SimpleCashSale_ShouldUpdateStockAndCalculateChange()
    {
        // 1. Setup Product
        var prod = new ProductDto("PROD-SIMPLE-01", "7501112223334", "Galletas de Chocolate", 8.00, 15.00, 0, 0, 30.0, "Abarrotes", "Pza", 0, null);
        await ProductService.AddAsync(prod);

        // 2. Prepare Cash Sale (2 items = $30.00, Paid with $50.00, Change = $20.00)
        var itemSnapshot = new SaleItemSnapshotDto(
            ProductId: "PROD-SIMPLE-01",
            Name: "Galletas de Chocolate",
            UnitPrice: 15.00,
            Cost: 8.00,
            Quantity: 2.0,
            Unit: "Pza",
            Category: "Abarrotes",
            Discount: 0.0,
            TotalPrice: 30.00
        );

        var saleDto = new SaleDto(
            Id: "SALE-CASH-SIMPLE-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 30.00,
            TotalCost: 16.00,
            Profit: 14.00,
            PaidAmount: 50.00,
            ChangeAmount: 20.00,
            PaymentMethod: "Efectivo",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            CashAmount: 30.00,
            CardAmount: 0.00
        );

        // 3. Execute Sale
        var savedSale = await _saleService.SaveAsync(saleDto);

        // 4. Verification
        Assert.NotNull(savedSale);
        Assert.Equal(30.00m, savedSale.Total);
        Assert.Equal(14.00m, savedSale.Profit);
        Assert.Equal(20.00m, savedSale.ChangeAmount);

        // Verify stock deducted (30 - 2 = 28)
        var updatedProd = await ProductService.GetByIdAsync("PROD-SIMPLE-01");
        Assert.Equal(28.0, updatedProd!.Stock);

        // Verify history contains sale
        var history = await _saleService.GetHistoryAsync(10);
        Assert.Contains(history, s => s.Id == savedSale.Id);
    }

    [Fact]
    public async Task Flow4_MixedPaymentSale_ShouldSplitCashAndCardPortionsAccurately()
    {
        // 1. Setup Product
        var prod = new ProductDto("PROD-MIXED-01", "7509998887776", "Café Gourmet 500g", 80.00, 150.00, 0, 0, 15.0, "Cafetería", "Pza", 0, null);
        await ProductService.AddAsync(prod);

        // 2. Prepare Mixed Sale Total = $300.00 (2 items). Split: Cash = $100.00, Card = $200.00
        var itemSnapshot = new SaleItemSnapshotDto(
            ProductId: "PROD-MIXED-01",
            Name: "Café Gourmet 500g",
            UnitPrice: 150.00,
            Cost: 80.00,
            Quantity: 2.0,
            Unit: "Pza",
            Category: "Cafetería",
            Discount: 0.0,
            TotalPrice: 300.00
        );

        var mixedSaleDto = new SaleDto(
            Id: "SALE-MIXED-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 300.00,
            TotalCost: 160.00,
            Profit: 140.00,
            PaidAmount: 300.00,
            ChangeAmount: 0.00,
            PaymentMethod: "Mixto",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            CashAmount: 100.00,
            CardAmount: 200.00
        );

        // 3. Execute Sale
        using (var bankProof = CreateContext()) CommercialTestSession.ConfirmCard(bankProof, mixedSaleDto.Id, mixedSaleDto.CardAmount);
        var savedSale = await _saleService.SaveAsync(mixedSaleDto);

        // 4. Verify Payment Split persistence
        Assert.NotNull(savedSale);
        Assert.Equal("Mixto", savedSale.PaymentMethod);
        Assert.Equal(100.00m, savedSale.CashAmount);
        Assert.Equal(200.00m, savedSale.CardAmount);

        // Stock deduction check (15 - 2 = 13)
        var updatedProd = await ProductService.GetByIdAsync("PROD-MIXED-01");
        Assert.Equal(13.0, updatedProd!.Stock);
    }

    [Fact]
    public async Task Flow5_CustomerCreditSaleAndPayment_ShouldTrackDebtAndHeadroom()
    {
        // 1. Create Customer with $2,000 credit limit
        var customer = new CustomerDto(
            Id: "CUST-CREDIT-01",
            Nombre: "Abarrotes Don José",
            Telefono: "8183334455",
            Email: "donjose@abarrotes.com",
            Rfc: "JOAB700512MK8",
            LimiteCredito: 2000.00,
            Deuda: 0.00
        );
        await CustomerService.AddAsync(customer);

        var prod = new ProductDto("PROD-BULK-01", "10055", "Caja de Aceite Vegetal", 250.00, 350.00, 0, 0, 10.0, "Abarrotes", "Caja", 0, null);
        await ProductService.AddAsync(prod);

        // 2. Perform Credit Sale of $700.00 (2 cajas @ $350)
        var itemSnapshot = new SaleItemSnapshotDto("PROD-BULK-01", "Caja de Aceite Vegetal", 350.00, 250.00, 2.0, "Caja", "Abarrotes", 0.0, 700.00);
        var creditSale = new SaleDto(
            Id: "SALE-CREDIT-DONJOSE",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 700.00,
            TotalCost: 500.00,
            Profit: 200.00,
            PaidAmount: 0.00,
            ChangeAmount: 0.00,
            PaymentMethod: "Crédito de Cliente",
            CustomerId: "CUST-CREDIT-01",
            IsCredit: true,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE"
        );

        await _saleService.SaveAsync(creditSale);

        // 3. Verify Customer Debt = $700, Available Credit = $1,300
        var custAfterSale = await CustomerService.GetByIdAsync("CUST-CREDIT-01");
        Assert.NotNull(custAfterSale);
        Assert.Equal(700.00m, custAfterSale.Debt);
        Assert.Equal(1300.00m, custAfterSale.AvailableCredit);

        // 4. Process Customer Payment (Abono de $400)
        var paymentResult = await CustomerService.RegisterCustomerPaymentAsync("CUST-CREDIT-01", 400.00m, "Efectivo", "Abono a cuenta folio SALE-CREDIT-DONJOSE", "ABONO-400");
        Assert.True(paymentResult);

        // 5. Verify Debt reduced to $300, Available Credit restored to $1,700
        var custAfterPayment = await CustomerService.GetByIdAsync("CUST-CREDIT-01");
        Assert.NotNull(custAfterPayment);
        Assert.Equal(300.00m, custAfterPayment.Debt);
        Assert.Equal(1700.00m, custAfterPayment.AvailableCredit);
    }

    [Fact]
    public async Task Flow6_FullShiftLifecycle_OpenRegisterSalesAndFinalZCutClosure()
    {
        // 1. Open shift with $500.00 initial float
        await ShiftService.CloseAsync("SHIFT-1", 0m);
        var activeShift = await ShiftService.OpenAsync(500.00m);
        Assert.NotNull(activeShift);
        Assert.Equal(500.00m, activeShift.OpeningBalance);
        Assert.Equal(1, activeShift.IsOpen);

        // 2. Perform Cash Sale of $250.00
        var prod = new ProductDto("PROD-SHIFT-01", "555666", "Pan Integral", 15.00, 25.00, 0, 0, 50.0, "Panadería", "Pza", 0, null);
        await ProductService.AddAsync(prod);

        var itemSnapshot = new SaleItemSnapshotDto("PROD-SHIFT-01", "Pan Integral", 25.00, 15.00, 10.0, "Pza", "Panadería", 0.0, 250.00);
        var saleDto = new SaleDto(
            Id: "SALE-SHIFT-01",
            Date: DateTimeOffset.UtcNow.ToString("o"),
            Items: [itemSnapshot],
            Total: 250.00,
            TotalCost: 150.00,
            Profit: 100.00,
            PaidAmount: 250.00,
            ChangeAmount: 0.00,
            PaymentMethod: "Efectivo",
            CustomerId: null,
            IsCredit: false,
            IsCancelled: false,
            CancelledAt: null,
            EstadoFiscal: "PENDIENTE",
            CashAmount: 250.00
        );
        await _saleService.SaveAsync(saleDto);

        // 3. Expected Cash in Drawer = $500 (Float) + $250 (Cash Sales) = $750.00
        // Execute Final Shift Closure (Z-Cut) with exact $750.00 count
        var closedShift = await ShiftService.CloseAsync(activeShift.Id, 750.00m);

        Assert.Equal(0, closedShift.IsOpen);
        Assert.Equal(750.00m, closedShift.ExpectedBalance);
        Assert.Equal(750.00m, closedShift.ActualBalance);
        Assert.Equal(0m, closedShift.Diff);

        // Verify active shift query returns null now that shift is closed
        var activeNow = await ShiftService.GetActiveAsync();
        Assert.Null(activeNow);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
