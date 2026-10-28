using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record PurchaseLineReq(Guid ProductId, decimal Quantity, decimal UnitCost);
public record CreatePurchaseReq(Guid OperationKey, Guid SupplierId, Guid LocationId, string? SupplierInvoice, int? CreditDays, List<PurchaseLineReq> Lines);
public record SupplierPaymentReq(Guid OperationKey, Guid SupplierId, Guid? PurchaseId, PayMethod Method, decimal Amount, string? Reference);
public record AdjustReq(Guid ProductId, Guid LocationId, decimal Quantity, bool IsOut, string Reason);

public class PurchaseService(AppDb db, CurrentUser user, AuditService audit, StockService stock, Numbering numbering)
{
    public async Task<Purchase> ReceiveAsync(CreatePurchaseReq r)
    {
        if (r.Lines is not { Count: > 0 }) throw ApiException.Bad("empty_purchase", "Agregue al menos un producto.");
        if (r.Lines.Any(l => l.Quantity <= 0 || l.UnitCost < 0)) throw ApiException.Bad("invalid_line", "Cantidad o costo inválido.");
        var dup = await db.Purchases.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.OperationKey == r.OperationKey);
        if (dup is not null) return dup;

        var company = await db.Companies.AsNoTracking().FirstAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var sup = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM \"Suppliers\" WHERE \"Id\" = {r.SupplierId} FOR UPDATE").FirstOrDefaultAsync()
                ?? throw ApiException.NotFound("Proveedor no encontrado.");
            var loc = await db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LocationId && l.Active) ?? throw ApiException.NotFound("Ubicación no encontrada.");
            var ids = r.Lines.Select(l => l.ProductId).Distinct().ToList();
            var products = await db.Products.Include(p => p.TaxRate).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            var pur = new Purchase { OperationKey = r.OperationKey, SupplierId = sup.Id, LocationId = loc.Id, SupplierInvoice = r.SupplierInvoice, UserId = user.Id };
            foreach (var l in r.Lines)
            {
                var p = products.GetValueOrDefault(l.ProductId) ?? throw ApiException.Bad("product_unavailable", "Producto no encontrado.");
                // En compras el costo se captura SIN impuesto; el impuesto se suma con la tasa del producto.
                var net = Money.R(l.Quantity * l.UnitCost);
                var tax = Money.R(net * p.TaxRate!.Rate);
                pur.Lines.Add(new PurchaseLine { ProductId = p.Id, ProductName = p.Name, Quantity = Money.Q(l.Quantity), UnitCost = l.UnitCost, TaxRate = p.TaxRate.Rate, NetAmount = net, TaxAmount = tax, LineTotal = net + tax });
            }
            pur.Subtotal = pur.Lines.Sum(x => x.NetAmount);
            pur.TaxTotal = pur.Lines.Sum(x => x.TaxAmount);
            pur.Total = pur.Subtotal + pur.TaxTotal;
            pur.DueDate = DateTime.UtcNow.AddDays(r.CreditDays ?? sup.CreditDays);
            pur.Number = await numbering.NextAsync("purchase", "C");
            db.Purchases.Add(pur);
            sup.Balance += pur.Total;
            await db.SaveChangesAsync();

            foreach (var g in pur.Lines.GroupBy(l => l.ProductId).OrderBy(g => g.Key))
            {
                var p = products[g.Key];
                var qty = g.Sum(x => x.Quantity);
                if (!p.TracksStock) continue;
                var bal = await stock.IncreaseAsync(p.Id, loc.Id, qty);
                // Costo: último costo de compra (decisión documentada; el costo promedio queda como mejora).
                p.Cost = g.Last().UnitCost;
                stock.Kardex(p.Id, loc.Id, MovementType.Purchase, qty, bal, g.Last().UnitCost, pur.Number, pur.Id, null, user.Id);
            }
            audit.Log("purchase.receive", "purchase", pur.Id, $"{pur.Number} total={pur.Total}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return pur;
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            return await db.Purchases.AsNoTracking().Include(p => p.Lines).FirstAsync(p => p.OperationKey == r.OperationKey);
        }
    }

    public async Task<(SupplierPayment Payment, decimal Balance)> PayAsync(SupplierPaymentReq r)
    {
        if (r.Amount <= 0) throw ApiException.Bad("invalid_amount", "El pago debe ser mayor que cero.");
        if (r.Method == PayMethod.Credit) throw ApiException.Bad("invalid_method", "Medio de pago inválido.");
        var dup = await db.SupplierPayments.AsNoTracking().FirstOrDefaultAsync(p => p.OperationKey == r.OperationKey);
        if (dup is not null) return (dup, (await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == dup.SupplierId)).Balance);
        var amount = Money.R(r.Amount);
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var sup = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM \"Suppliers\" WHERE \"Id\" = {r.SupplierId} FOR UPDATE").FirstOrDefaultAsync()
                ?? throw ApiException.NotFound("Proveedor no encontrado.");
            if (amount > sup.Balance) throw ApiException.Conflict("overpayment", $"El pago excede la deuda con el proveedor ({sup.Balance:0.00}).");
            var pending = await db.Purchases.FromSqlInterpolated($"SELECT * FROM \"Purchases\" WHERE \"SupplierId\" = {sup.Id} AND \"Total\" > \"Paid\" ORDER BY \"ReceivedAt\" FOR UPDATE").ToListAsync();
            if (r.PurchaseId is not null) pending = pending.Where(p => p.Id == r.PurchaseId).ToList();
            var left = amount;
            foreach (var p in pending)
            {
                if (left <= 0) break;
                var a = Math.Min(left, p.Total - p.Paid);
                p.Paid += a; left -= a;
            }
            if (left > 0) throw ApiException.Conflict("overpayment", "El pago excede el saldo de la compra seleccionada.");
            sup.Balance -= amount;
            var pay = new SupplierPayment { OperationKey = r.OperationKey, SupplierId = sup.Id, PurchaseId = r.PurchaseId, Method = r.Method, Amount = amount, Reference = r.Reference, UserId = user.Id };
            db.SupplierPayments.Add(pay);
            audit.Log("supplier.payment", "supplier", sup.Id, $"{amount} {r.Method}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (pay, sup.Balance);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var again = await db.SupplierPayments.AsNoTracking().FirstAsync(p => p.OperationKey == r.OperationKey);
            return (again, (await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == again.SupplierId)).Balance);
        }
    }

    public async Task AdjustAsync(AdjustReq r)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) throw ApiException.Bad("reason_required", "El ajuste requiere una justificación.");
        if (r.Quantity <= 0) throw ApiException.Bad("invalid_quantity", "La cantidad debe ser mayor que cero.");
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.ProductId) ?? throw ApiException.NotFound("Producto no encontrado.");
        var qty = Money.Q(r.Quantity);
        await using var tx = await db.Database.BeginTransactionAsync();
        decimal bal;
        if (r.IsOut) bal = await stock.DecreaseAsync(p.Id, r.LocationId, qty, false, p.Name);
        else bal = await stock.IncreaseAsync(p.Id, r.LocationId, qty);
        stock.Kardex(p.Id, r.LocationId, r.IsOut ? MovementType.AdjustmentOut : MovementType.AdjustmentIn, r.IsOut ? -qty : qty, bal, p.Cost, "AJUSTE", null, r.Reason.Trim(), user.Id);
        audit.Log("inventory.adjust", "product", p.Id, $"{(r.IsOut ? "-" : "+")}{qty} motivo={r.Reason}");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
