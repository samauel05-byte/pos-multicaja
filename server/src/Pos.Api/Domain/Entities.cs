namespace Pos.Api.Domain;

public enum Role { Admin = 1, Supervisor = 2, Cashier = 3 }
public enum PayMethod { Cash = 1, Card = 2, Transfer = 3, Credit = 4 }
public enum SaleStatus { Completed = 1, Voided = 2 }
public enum ShiftStatus { Open = 1, Closed = 2 }
public enum CashMovementType
{
    Opening = 1,      // fondo inicial
    CashIn = 2,       // entrada manual
    CashOut = 3,      // salida manual
    SaleCash = 4,     // efectivo neto de venta (aplicado, ya descontado el cambio)
    RefundCash = 5,   // devolución en efectivo
    CustomerPaymentCash = 6, // abono en efectivo
}
public enum MovementType
{
    Purchase = 1, Sale = 2, Return = 3, Void = 4, AdjustmentIn = 5, AdjustmentOut = 6, TransferIn = 7, TransferOut = 8, Initial = 9,
}
public enum QuoteStatus { Open = 1, Converted = 2, Expired = 3, Cancelled = 4 }
public enum PrintStatus { Pending = 1, Printed = 2, Failed = 3 }

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Company : Entity
{
    public string Name { get; set; } = "";
    public string? TaxId { get; set; }          // RNC (ficticio en demo)
    public string CurrencyCode { get; set; } = "DOP";
    public string CurrencySymbol { get; set; } = "RD$";
    public bool PricesIncludeTax { get; set; } = true;
    public bool AllowNegativeStock { get; set; } = false;
    public decimal CashierMaxDiscountPercent { get; set; } = 5m;
    public decimal SupervisorMaxDiscountPercent { get; set; } = 30m;
    public decimal CashDifferenceToleranceAmount { get; set; } = 50m; // sobre esto se exige comentario
    public string? TicketFooter { get; set; } = "Gracias por su compra";
    public bool FiscalModuleEnabled { get; set; } = false; // módulo fiscal DO: pendiente de validar
}

public class Branch : Entity
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public bool Active { get; set; } = true;
}

public class Location : Entity   // almacén / ubicación de inventario dentro de una sucursal
{
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Active { get; set; } = true;
}

public class Register : Entity   // caja física
{
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
    public Guid LocationId { get; set; }  // de dónde descuenta inventario
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public string? PrinterName { get; set; }
}

public class AppUser : Entity
{
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public Role Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? PinHash { get; set; }   // PIN de autorización (supervisor/admin)
    public bool Active { get; set; } = true;
    public Guid? BranchId { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public class Category : Entity
{
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class Unit : Entity
{
    public string Name { get; set; } = "";
    public string Abbreviation { get; set; } = "";
    public bool AllowsDecimals { get; set; }
}

public class TaxRate : Entity
{
    public string Name { get; set; } = "";
    public decimal Rate { get; set; }       // 0.18 = 18%
    public bool Active { get; set; } = true;
}

public class Product : Entity
{
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    public Guid UnitId { get; set; }
    public Unit? Unit { get; set; }
    public Guid TaxRateId { get; set; }
    public TaxRate? TaxRate { get; set; }
    public decimal Cost { get; set; }
    public decimal Price { get; set; }
    public decimal MinStock { get; set; }
    public bool TracksStock { get; set; } = true;
    public bool Active { get; set; } = true;
}

public class StockLevel   // existencia de un producto en una ubicación
{
    public Guid ProductId { get; set; }
    public Guid LocationId { get; set; }
    public decimal Quantity { get; set; }
}

public class InventoryMovement : Entity   // kardex
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid LocationId { get; set; }
    public MovementType Type { get; set; }
    public decimal Quantity { get; set; }       // con signo: + entra, - sale
    public decimal BalanceAfter { get; set; }
    public decimal UnitCost { get; set; }
    public string? Reference { get; set; }      // ticket, compra, ajuste
    public Guid? DocumentId { get; set; }
    public string? Reason { get; set; }
    public Guid? UserId { get; set; }
}

public class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public decimal CreditLimit { get; set; }
    public int CreditDays { get; set; } = 30;
    public decimal Balance { get; set; }   // saldo pendiente (desnormalizado, se actualiza en transacción)
    public bool Active { get; set; } = true;
}

public class Supplier : Entity
{
    public string Name { get; set; } = "";
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public int CreditDays { get; set; } = 30;
    public decimal Balance { get; set; }
    public bool Active { get; set; } = true;
}

public class Shift : Entity   // turno de caja
{
    public Guid RegisterId { get; set; }
    public Register? Register { get; set; }
    public Guid OpenedByUserId { get; set; }
    public AppUser? OpenedBy { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public decimal OpeningFloat { get; set; }
    public ShiftStatus Status { get; set; } = ShiftStatus.Open;
    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    // Cierre (fotografía al momento de cerrar)
    public decimal? ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? CashDifference { get; set; }
    public decimal? CardTotal { get; set; }
    public decimal? TransferTotal { get; set; }
    public decimal? CreditTotal { get; set; }
    public string? CloseNotes { get; set; }
    public string? CountDetailJson { get; set; }   // conteo por denominación
}

public class CashMovement : Entity
{
    public Guid ShiftId { get; set; }
    public CashMovementType Type { get; set; }
    public decimal Amount { get; set; }   // con signo respecto a la gaveta
    public string? Reason { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid UserId { get; set; }
}

public class Sale : Entity
{
    public string Number { get; set; } = "";       // consecutivo legible, ej. T-000001
    public Guid OperationKey { get; set; }         // idempotencia (único)
    public Guid BranchId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public decimal Subtotal { get; set; }          // base sin impuesto, tras descuentos
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal PaidTotal { get; set; }         // aplicado a la venta
    public decimal CreditAmount { get; set; }      // parte a crédito
    public decimal CreditBalance { get; set; }     // saldo pendiente de la parte a crédito
    public DateTime? DueDate { get; set; }
    public decimal ChangeGiven { get; set; }
    public bool PricesIncludeTax { get; set; }
    public string CurrencySymbol { get; set; } = "RD$";
    public Guid? QuoteId { get; set; }
    public Guid? DiscountAuthorizedBy { get; set; }
    public int PrintCount { get; set; }
    public string? VoidReason { get; set; }
    public DateTime? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public List<SaleLine> Lines { get; set; } = new();
    public List<SalePayment> Payments { get; set; } = new();
}

public class SaleLine : Entity
{
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    // Historial: copia de datos del producto al momento de la venta
    public string ProductName { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal UnitCost { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal NetAmount { get; set; }   // base tras descuento
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
    public decimal ReturnedQuantity { get; set; }
}

public class SalePayment : Entity
{
    public Guid SaleId { get; set; }
    public PayMethod Method { get; set; }
    public decimal Amount { get; set; }      // aplicado a la venta
    public decimal Tendered { get; set; }    // entregado (solo efectivo puede exceder Amount)
    public string? Reference { get; set; }   // últimos 4 dígitos, # transferencia. NO es un cargo bancario.
}

public class Quote : Entity
{
    public string Number { get; set; } = "";
    public Guid UserId { get; set; }
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public QuoteStatus Status { get; set; } = QuoteStatus.Open;
    public DateTime ValidUntil { get; set; }
    public string? Notes { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public Guid? SaleId { get; set; }
    public List<QuoteLine> Lines { get; set; } = new();
}

public class QuoteLine : Entity
{
    public Guid QuoteId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
}

public class SaleReturn : Entity
{
    public string Number { get; set; } = "";
    public Guid OperationKey { get; set; }
    public Guid SaleId { get; set; }
    public Sale? Sale { get; set; }
    public Guid ShiftId { get; set; }
    public Guid UserId { get; set; }
    public Guid AuthorizedBy { get; set; }
    public string Reason { get; set; } = "";
    public bool IsVoid { get; set; }
    public decimal Total { get; set; }
    public bool Restock { get; set; } = true;
    public List<ReturnLine> Lines { get; set; } = new();
    public List<ReturnRefund> Refunds { get; set; } = new();
}

public class ReturnRefund : Entity
{
    public Guid ReturnId { get; set; }
    public PayMethod Method { get; set; }   // Credit = reducción del saldo del cliente
    public decimal Amount { get; set; }
    public string? Reference { get; set; }  // Tarjeta/transferencia: registro manual, no hay reverso bancario automático
}

public class ReturnLine : Entity
{
    public Guid ReturnId { get; set; }
    public Guid SaleLineId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
}

public class CustomerPayment : Entity   // abono de cliente
{
    public string Number { get; set; } = "";
    public Guid OperationKey { get; set; }
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid UserId { get; set; }
    public PayMethod Method { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public List<CustomerPaymentAllocation> Allocations { get; set; } = new();
}

public class CustomerPaymentAllocation : Entity
{
    public Guid CustomerPaymentId { get; set; }
    public Guid SaleId { get; set; }
    public decimal Amount { get; set; }
}

public class Purchase : Entity
{
    public string Number { get; set; } = "";
    public Guid OperationKey { get; set; }
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public Guid LocationId { get; set; }
    public string? SupplierInvoice { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime DueDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public Guid UserId { get; set; }
    public List<PurchaseLine> Lines { get; set; } = new();
}

public class PurchaseLine : Entity
{
    public Guid PurchaseId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxRate { get; set; }
    public decimal NetAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
}

public class SupplierPayment : Entity
{
    public Guid SupplierId { get; set; }
    public Guid? PurchaseId { get; set; }
    public Guid OperationKey { get; set; }
    public PayMethod Method { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
    public Guid UserId { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public string? Ip { get; set; }
}

public class PrintJob : Entity
{
    public Guid SaleId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid UserId { get; set; }
    public bool IsReprint { get; set; }
    public PrintStatus Status { get; set; } = PrintStatus.Pending;
    public string? Error { get; set; }
}

public class DocumentCounter
{
    public string Key { get; set; } = "";
    public long Value { get; set; }
}

public class BackupRecord : Entity
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Kind { get; set; } = "auto";   // auto | manual
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>Bandeja de salida transaccional: eventos de negocio escritos en la MISMA transacción que la venta/devolución.
/// Preparación para integraciones (fiscal, tienda en línea). No hay ningún consumidor implementado todavía.</summary>
public class OutboxEvent
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";          // sale.completed | sale.returned | sale.voided
    public Guid AggregateId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
