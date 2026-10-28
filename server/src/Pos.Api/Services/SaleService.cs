using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record SaleLineReq(Guid ProductId, decimal Quantity, decimal? DiscountPercent, decimal? DiscountAmount);
public record PaymentReq(PayMethod Method, decimal Amount, decimal? Tendered, string? Reference);
public record CreateSaleReq(Guid OperationKey, Guid RegisterId, Guid? CustomerId, List<SaleLineReq> Lines, List<PaymentReq> Payments,
    Guid? QuoteId, string? AuthorizerUsername, string? AuthorizerPin);

public record SaleLineDto(Guid Id, Guid ProductId, string ProductName, string Sku, decimal Quantity, decimal UnitPrice, decimal DiscountPercent,
    decimal DiscountAmount, decimal TaxRate, decimal NetAmount, decimal TaxAmount, decimal LineTotal, decimal ReturnedQuantity);
public record SalePaymentDto(PayMethod Method, decimal Amount, decimal Tendered, string? Reference);
public record SaleDto(Guid Id, string Number, Guid OperationKey, DateTime CreatedAt, SaleStatus Status, decimal Subtotal, decimal DiscountTotal,
    decimal TaxTotal, decimal Total, decimal PaidTotal, decimal CreditAmount, decimal CreditBalance, DateTime? DueDate, decimal ChangeGiven,
    string CurrencySymbol, bool PricesIncludeTax, string? CustomerName, Guid? CustomerId, string CashierName, string RegisterName, Guid RegisterId, Guid ShiftId,
    int PrintCount, string? VoidReason, List<SaleLineDto> Lines, List<SalePaymentDto> Payments);

public class SaleService(AppDb db, CurrentUser user, AuditService audit, AuthorizerService authorizer, StockService stock, Numbering numbering)
{
    public async Task<(SaleDto Sale, bool Duplicate)> CreateAsync(CreateSaleReq r)
    {
        if (r.OperationKey == Guid.Empty) throw ApiException.Bad("operation_key_required", "Falta la clave de operación.");
        if (r.Lines is not { Count: > 0 }) throw ApiException.Bad("empty_cart", "El carrito está vacío.");
        if (r.Lines.Any(l => l.Quantity <= 0)) throw ApiException.Bad("invalid_quantity", "Todas las cantidades deben ser mayores que cero.");

        // Reenvío del mismo cobro: se devuelve la venta ya registrada, nunca se crea otra.
        var existing = await db.Sales.AsNoTracking().Where(s => s.OperationKey == r.OperationKey).Select(s => s.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty) return (await GetDtoAsync(existing), true);

        var reg = await db.Registers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.RegisterId && x.Active)
            ?? throw ApiException.NotFound("Caja no encontrada.");
        var company = await db.Companies.AsNoTracking().FirstAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // Bloqueo compartido del turno: un cierre simultáneo (FOR UPDATE) espera a que termine esta venta.
            var shift = await db.Shifts.FromSqlInterpolated($"SELECT * FROM \"Shifts\" WHERE \"RegisterId\" = {reg.Id} AND \"Status\" = 1 FOR SHARE").FirstOrDefaultAsync()
                ?? throw ApiException.Conflict("no_open_shift", "La caja no tiene un turno abierto. Abra la caja antes de cobrar.");
            if (user.Role == Role.Cashier && shift.OpenedByUserId != user.Id)
                throw ApiException.Forbidden("El turno de esta caja pertenece a otro cajero.");

            var ids = r.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Include(p => p.TaxRate).Include(p => p.Unit).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            Quote? quote = null;
            if (r.QuoteId is not null)
            {
                quote = await db.Quotes.FromSqlInterpolated($"SELECT * FROM \"Quotes\" WHERE \"Id\" = {r.QuoteId} FOR UPDATE").FirstOrDefaultAsync()
                    ?? throw ApiException.NotFound("Cotización no encontrada.");
                if (quote.Status != QuoteStatus.Open) throw ApiException.Conflict("quote_not_open", "La cotización ya no está disponible.");
                if (quote.ValidUntil < DateTime.UtcNow) throw ApiException.Conflict("quote_expired", "La cotización está vencida.");
            }

            var sale = new Sale
            {
                OperationKey = r.OperationKey, BranchId = reg.BranchId, RegisterId = reg.Id, ShiftId = shift.Id, UserId = user.Id,
                CustomerId = r.CustomerId, PricesIncludeTax = company.PricesIncludeTax, CurrencySymbol = company.CurrencySymbol, QuoteId = r.QuoteId,
            };

            decimal maxPct = 0;
            foreach (var l in r.Lines)
            {
                if (!products.TryGetValue(l.ProductId, out var p) || !p.Active) throw ApiException.Bad("product_unavailable", "Producto no disponible.");
                if (!p.Unit!.AllowsDecimals && l.Quantity != decimal.Truncate(l.Quantity))
                    throw ApiException.Bad("quantity_not_integer", $"\"{p.Name}\" solo admite cantidades enteras.");
                var qty = Money.Q(l.Quantity);
                // El precio SIEMPRE viene de la base de datos; el cliente no lo envía.
                var c = Pricing.Line(qty, p.Price, l.DiscountPercent, l.DiscountAmount, p.TaxRate!.Rate, company.PricesIncludeTax);
                maxPct = Math.Max(maxPct, c.EffectivePercent);
                sale.Lines.Add(new SaleLine
                {
                    ProductId = p.Id, ProductName = p.Name, Sku = p.Sku, Quantity = qty, UnitPrice = p.Price, UnitCost = p.Cost,
                    DiscountPercent = c.EffectivePercent, DiscountAmount = c.Discount, TaxRate = p.TaxRate.Rate,
                    NetAmount = c.Net, TaxAmount = c.Tax, LineTotal = c.Total,
                });
            }

            // Descuentos: el cajero tiene un tope; por encima se exige PIN de supervisor, con tope propio.
            var ownLimit = user.Role == Role.Cashier ? company.CashierMaxDiscountPercent : company.SupervisorMaxDiscountPercent;
            if (maxPct > ownLimit)
            {
                var auth = await authorizer.VerifyAsync(r.AuthorizerUsername, r.AuthorizerPin, $"descuento {maxPct}%");
                if (maxPct > company.SupervisorMaxDiscountPercent)
                    throw ApiException.Bad("discount_over_limit", $"El descuento máximo permitido es {company.SupervisorMaxDiscountPercent}%.");
                sale.DiscountAuthorizedBy = auth.Id;
            }

            sale.DiscountTotal = sale.Lines.Sum(x => x.DiscountAmount);
            sale.Subtotal = sale.Lines.Sum(x => x.NetAmount);
            sale.TaxTotal = sale.Lines.Sum(x => x.TaxAmount);
            sale.Total = sale.Lines.Sum(x => x.LineTotal);
            if (sale.Total <= 0) throw ApiException.Bad("invalid_total", "El total de la venta debe ser mayor que cero.");

            // Pagos
            var nonCash = r.Payments.Where(p => p.Method != PayMethod.Cash).ToList();
            if (nonCash.Any(p => p.Amount <= 0)) throw ApiException.Bad("invalid_payment", "Los pagos deben ser mayores que cero.");
            var nonCashSum = nonCash.Sum(p => Money.R(p.Amount));
            if (nonCashSum > sale.Total) throw ApiException.Bad("overpayment", "Los pagos con tarjeta, transferencia o crédito no pueden exceder el total.");
            var cashDue = sale.Total - nonCashSum;
            var tendered = r.Payments.Where(p => p.Method == PayMethod.Cash).Sum(p => Money.R(p.Tendered ?? p.Amount));
            if (cashDue > 0 && tendered < cashDue)
                throw ApiException.Bad("insufficient_payment", $"Falta cobrar {(cashDue - tendered):0.00}.");
            if (cashDue == 0 && tendered > 0)
                throw ApiException.Bad("cash_not_needed", "El total ya está cubierto sin efectivo.");

            foreach (var p in nonCash)
                sale.Payments.Add(new SalePayment { Method = p.Method, Amount = Money.R(p.Amount), Tendered = Money.R(p.Amount), Reference = p.Reference?.Trim() });
            if (cashDue > 0)
            {
                sale.Payments.Add(new SalePayment { Method = PayMethod.Cash, Amount = cashDue, Tendered = tendered });
                sale.ChangeGiven = tendered - cashDue;
            }
            sale.PaidTotal = sale.Total;

            // Crédito
            sale.CreditAmount = sale.Payments.Where(p => p.Method == PayMethod.Credit).Sum(p => p.Amount);
            Customer? customer = null;
            if (r.CustomerId is not null)
                customer = await db.Customers.FromSqlInterpolated($"SELECT * FROM \"Customers\" WHERE \"Id\" = {r.CustomerId} FOR UPDATE").FirstOrDefaultAsync()
                    ?? throw ApiException.NotFound("Cliente no encontrado.");
            if (sale.CreditAmount > 0)
            {
                if (customer is null) throw ApiException.Bad("customer_required", "La venta a crédito requiere un cliente.");
                if (!customer.Active) throw ApiException.Conflict("customer_inactive", "El cliente está inactivo.");
                if (customer.Balance + sale.CreditAmount > customer.CreditLimit)
                    throw ApiException.Conflict("credit_limit_exceeded",
                        $"Límite de crédito excedido. Disponible: {(customer.CreditLimit - customer.Balance):0.00}.");
                sale.CreditBalance = sale.CreditAmount;
                sale.DueDate = DateTime.UtcNow.AddDays(customer.CreditDays);
                customer.Balance += sale.CreditAmount;
            }

            sale.Number = await numbering.NextAsync("sale", "T");
            db.Sales.Add(sale);
            await db.SaveChangesAsync();   // clave única de operación: si otra petición idéntica ganó, falla aquí y se revierte todo

            // Inventario (atómico por producto, en orden estable para evitar interbloqueos)
            foreach (var g in sale.Lines.GroupBy(l => l.ProductId).OrderBy(g => g.Key))
            {
                var p = products[g.Key];
                if (!p.TracksStock) continue;
                var qty = g.Sum(x => x.Quantity);
                var bal = await stock.DecreaseAsync(p.Id, reg.LocationId, qty, company.AllowNegativeStock, p.Name);
                stock.Kardex(p.Id, reg.LocationId, MovementType.Sale, -qty, bal, p.Cost, sale.Number, sale.Id, null, user.Id);
            }

            var cashApplied = cashDue;
            if (cashApplied > 0)
                db.CashMovements.Add(new CashMovement { ShiftId = shift.Id, Type = CashMovementType.SaleCash, Amount = cashApplied, DocumentId = sale.Id, Reason = sale.Number, UserId = user.Id });

            if (quote is not null) { quote.Status = QuoteStatus.Converted; quote.SaleId = sale.Id; }
            db.Outbox.Add(new OutboxEvent { Type = "sale.completed", AggregateId = sale.Id, PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                sale.Number, sale.Total, sale.TaxTotal, sale.CurrencySymbol, customerId = sale.CustomerId, createdAt = sale.CreatedAt,
                lines = sale.Lines.Select(l => new { l.Sku, l.ProductName, l.Quantity, l.UnitPrice, l.TaxRate, l.LineTotal }),
                payments = sale.Payments.Select(p => new { method = p.Method.ToString(), p.Amount }),
            }) });
            audit.Log("sale.create", "sale", sale.Id, $"{sale.Number} total={sale.Total}" + (sale.DiscountAuthorizedBy is null ? "" : $" descuento autorizado por {sale.DiscountAuthorizedBy}"));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (await GetDtoAsync(sale.Id), false);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var again = await db.Sales.AsNoTracking().Where(s => s.OperationKey == r.OperationKey).Select(s => s.Id).FirstOrDefaultAsync();
            if (again != Guid.Empty) return (await GetDtoAsync(again), true);
            throw;
        }
    }

    public async Task<SaleDto?> FindByOperationAsync(Guid key)
    {
        var id = await db.Sales.AsNoTracking().Where(s => s.OperationKey == key).Select(s => s.Id).FirstOrDefaultAsync();
        return id == Guid.Empty ? null : await GetDtoAsync(id);
    }

    public async Task<SaleDto> GetDtoAsync(Guid id)
    {
        var s = await db.Sales.AsNoTracking().Include(x => x.Lines).Include(x => x.Payments).Include(x => x.Customer).Include(x => x.User)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Venta no encontrada.");
        var reg = await db.Registers.AsNoTracking().FirstAsync(x => x.Id == s.RegisterId);
        return new SaleDto(s.Id, s.Number, s.OperationKey, s.CreatedAt, s.Status, s.Subtotal, s.DiscountTotal, s.TaxTotal, s.Total, s.PaidTotal,
            s.CreditAmount, s.CreditBalance, s.DueDate, s.ChangeGiven, s.CurrencySymbol, s.PricesIncludeTax, s.Customer?.Name, s.CustomerId,
            s.User!.FullName, reg.Name, s.RegisterId, s.ShiftId, s.PrintCount, s.VoidReason,
            s.Lines.OrderBy(l => l.CreatedAt).Select(l => new SaleLineDto(l.Id, l.ProductId, l.ProductName, l.Sku, l.Quantity, l.UnitPrice, l.DiscountPercent,
                l.DiscountAmount, l.TaxRate, l.NetAmount, l.TaxAmount, l.LineTotal, l.ReturnedQuantity)).ToList(),
            s.Payments.Select(p => new SalePaymentDto(p.Method, p.Amount, p.Tendered, p.Reference)).ToList());
    }
}
