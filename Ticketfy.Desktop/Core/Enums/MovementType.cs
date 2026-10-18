namespace Ticketfy.Core.Enums;

public enum MovementType
{
    AperturaCaja = 0,
    VentaEfectivo = 1,
    AbonoCliente = 2,
    GastoOperativo = 3,    // Cash Outflow: Petty cash / operational expenses
    CompraEfectivo = 4,    // Cash Outflow: Supplier payments from drawer
    DevolucionCliente = 5, // Cash Outflow: Cash refunds to customers
    RetiroEfectivo = 6,    // Manual drop / safe deposit
    CierreCaja = 7,
    IngresoExtraordinario = 8,
    RecargaMonedero = 9,
    CancelacionGasto = 10,

    // Backward compatibility aliases
    Venta = 1,
    Gasto = 3,
    Retiro = 6,
    Deposito = 0
}
