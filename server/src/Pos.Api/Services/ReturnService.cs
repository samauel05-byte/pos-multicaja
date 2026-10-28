using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record ReturnLineReq(Guid SaleLineId, decimal Quantity);
public record RefundReq(PayMethod Method, decimal? Amount, string? Reference);
public record CreateReturnReq(Guid OperationKey, Guid SaleId, Guid RegisterId, string Reason, bool Restock, List<ReturnLineReq> Lines,
    PayMethod? RefundMethod, string? RefundReference, string? AuthorizerUsername, string? AuthorizerPin);
public record VoidSaleReq(Guid OperationKey, Guid RegisterId, string Reason, string? AuthorizerUsername, string? AuthorizerPin);
public record ReturnDto(Guid Id, string Number, string SaleNumber, DateTime CreatedAt, string Reason, bool IsVoid, decimal Total,
    List<(PayMethod Method, decimal Amount)> Refunds);

/// <summary>
/// Devoluciones y anulaciones. Nunca borran la venta: crean un documento compensatorio con movimientos de inventario y caja.
/// </summary>
public class ReturnService(AppDb db, CurrentUser user, AuditService audit, AuthorizerService authorizer, StockService stock, Numbering numbering)
{
    public Task<ReturnDto> ReturnAsync(CreateReturnReq r) => RunAsync(r.OperationKey, r.SaleId, r.RegisterId, r.Reason, r.Restock, false, r.Lines,
        r.RefundMethod, r.RefundReference, r.AuthorizerUsername, r.AuthorizerPin);

    public Task<ReturnDto> VoidAsync(Guid saleId, VoidSaleReq r) => RunAsync(r.OperationKey, saleId, r.RegisterId, r.Reason, true, true, null,
        null, null, r.AuthorizerUsername, r.AuthorizerPin);

    async Task<ReturnDto> RunAsync(Guid opKey, Guid saleId, Guid registerId, string reason, bool restock, bool isVoid, List<ReturnLineReq>? reqLines,
        PayMethod? refundMethod, string? refundRef, string? authUser, string? authPin)
    {
        if (opKey == Guid.Empty) throw ApiException.Bad("operation_key_required", "Falta la clave de operación.");
        if (string.IsNullOrWhiteSpace(reason)) throw ApiException.Bad("reason_required", "El motivo es obligatorio.");

        var dup = await db.Returns.AsNoTracking().Include(x => x.Refunds).Include(x => x.Sale).FirstOrDefaultAsync(x => x.OperationKey == opKey);
        if (dup is not null) return Dto(dup);

        // Permiso: supervisor/admin directamente; cajero solo con PIN de supervisor.
        Guid authorizedBy;
        var perm = isVoid ? Perm.SalesVoid : Perm.ReturnsCreate;
        if (user.Can(perm)) authorizedBy = user.Id;
        else authorizedBy = (await authorizer.VerifyAsync(authUser, authPin, isVoid ? "anulación" : "devolución")).Id;

        var reg = await db.Registers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == registerId && x.Active) ?? throw ApiException.NotFound("Caja no encontrada.");

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var shift = await db.Shifts.FromSqlInterpolated($"SELECT * FROM \"Shifts\" WHERE \"RegisterId\" = {reg.Id} AND \"Status\" = 1 FOR SHARE").FirstOrDefaultAsync()
                ?? throw ApiException.Conflict("no_open_shift", "Abra un turno en la caja para registrar devoluciones.");

            var sale = await db.Sales.FromSqlInterpolated($"SELECT * FROM \"Sales\" WHERE \"Id\" = {saleId} FOR UPDATE").Include(s => s.Lines).Include(s => s.Payments)
                .AsSplitQuery().FirstOrDefaultAsync() ?? throw ApiException.NotFound("Venta no encontrada.");
            if (sale.Status == SaleStatus.Voided) throw ApiException.Conflict("sale_voided", "La venta ya fue anulada.");

            var company = await db.Companies.AsNoTracking().FirstAsync();
            var ret = new SaleReturn
            {
                OperationKey = opKey, SaleId = sale.Id, ShiftId = shift.Id, UserId = user.Id, AuthorizedBy = authorizedBy,
                Reason = reason.Trim(), IsVoid = isVoid, Restock = restock,
            };

            if (isVoid)
            {
                if (sale.Lines.Any(l => l.ReturnedQuantity > 0))
                    throw ApiException.Conflict("sale_has_returns", "La venta tiene devoluciones; use devolución por las líneas restantes.");
                foreach (var l in sale.Lines) AddReturnLine(ret, l, l.Quantity);
            }
            else
            {
                if (reqLines is not { Count: > 0 }) throw ApiException.Bad("empty_return", "Seleccione al menos una línea.");
                foreach (var rl in reqLines)
                {
                    var l = sale.Lines.FirstOrDefault(x => x.Id == rl.SaleLineId) ?? throw ApiException.Bad("line_not_found", "Línea de venta inválida.");
                    if (rl.Quantity <= 0) throw ApiException.Bad("invalid_quantity", "Cantidad inválida.");
                    if (rl.Quantity > l.Quantity - l.ReturnedQuantity)
                        throw ApiException.Conflict("over_return", $"No se puede devolver más de lo vendido de \"{l.ProductName}\" (restante: {l.Quantity - l.ReturnedQuantity:0.####}).");
                    AddReturnLine(ret, l, rl.Quantity);
                }
            }
            ret.Total = ret.Lines.Sum(x => x.LineTotal);

            // Orden de reembolso: primero se reduce el saldo a crédito pendiente de la venta; el resto sale por los medios elegidos.
            var remaining = ret.Total;
            var creditReduce = Math.Min(remaining, sale.CreditBalance);
            if (creditReduce > 0)
            {
                ret.Refunds.Add(new ReturnRefund { Method = PayMethod.Credit, Amount = creditReduce });
                sale.CreditBalance -= creditReduce;
                if (sale.CustomerId is not null)
                {
                    var c = await db.Customers.FromSqlInterpolated($"SELECT * FROM \"Customers\" WHERE \"Id\" = {sale.CustomerId} FOR UPDATE").FirstAsync();
                    c.Balance -= creditReduce;
                }
                remaining -= creditReduce;
            }
            if (remaining > 0)
            {
                if (isVoid)
                {
                    // Anulación: se reembolsa por los mismos medios en que se pagó (proporcional a lo cobrado, sin crédito).
                    var paid = sale.Payments.Where(p => p.Method != PayMethod.Credit).ToList();
                    var basis = paid.Sum(p => p.Amount);
                    decimal left = remaining;
                    for (var i = 0; i < paid.Count; i++)
                    {
                        var amt = i == paid.Count - 1 ? left : Math.Min(left, Money.R(remaining * paid[i].Amount / basis));
                        if (amt > 0) ret.Refunds.Add(new ReturnRefund { Method = paid[i].Method, Amount = amt, Reference = paid[i].Reference });
                        left -= amt;
                    }
                }
                else
                {
                    var m = refundMethod ?? PayMethod.Cash;
                    if (m == PayMethod.Credit) throw ApiException.Bad("invalid_refund_method", "Elija efectivo, tarjeta o transferencia.");
                    ret.Refunds.Add(new ReturnRefund { Method = m, Amount = remaining, Reference = refundRef });
                }
            }

            var cashRefund = ret.Refunds.Where(x => x.Method == PayMethod.Cash).Sum(x => x.Amount);
            if (cashRefund > 0)
            {
                var expected = await db.CashMovements.Where(m => m.ShiftId == shift.Id).SumAsync(m => (decimal?)m.Amount) ?? 0;
                if (expected < cashRefund) throw ApiException.Conflict("insufficient_cash", "No hay efectivo suficiente en caja para este reembolso.");
            }

            ret.Number = await numbering.NextAsync(isVoid ? "void" : "return", isVoid ? "A" : "D");
            db.Returns.Add(ret);
            await db.SaveChangesAsync();   // clave única de operación

            // Actualiza cantidades devueltas y reingresa inventario
            foreach (var rl in ret.Lines.OrderBy(x => x.ProductId))
            {
                var l = sale.Lines.First(x => x.Id == rl.SaleLineId);
                l.ReturnedQuantity += rl.Quantity;
                var prod = await db.Products.AsNoTracking().FirstAsync(p => p.Id == l.ProductId);
                if (restock && prod.TracksStock)
                {
                    var bal = await stock.IncreaseAsync(l.ProductId, reg.LocationId, rl.Quantity);
                    stock.Kardex(l.ProductId, reg.LocationId, isVoid ? MovementType.Void : MovementType.Return, rl.Quantity, bal, l.UnitCost, ret.Number, ret.Id, reason, user.Id);
                }
            }
            if (cashRefund > 0)
                db.CashMovements.Add(new CashMovement { ShiftId = shift.Id, Type = CashMovementType.RefundCash, Amount = -cashRefund, DocumentId = ret.Id, Reason = ret.Number, UserId = user.Id });

            if (isVoid) { sale.Status = SaleStatus.Voided; sale.VoidReason = reason.Trim(); sale.VoidedAt = DateTime.UtcNow; sale.VoidedBy = user.Id; }
            db.Outbox.Add(new OutboxEvent { Type = isVoid ? "sale.voided" : "sale.returned", AggregateId = ret.Id, PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            { ret.Number, saleNumber = sale.Number, ret.Total, ret.Reason, lines = ret.Lines.Select(l => new { l.SaleLineId, l.Quantity, l.LineTotal }) }) });
            audit.Log(isVoid ? "sale.void" : "sale.return", "sale", sale.Id, $"{ret.Number} total={ret.Total} motivo={reason} autoriza={authorizedBy}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            ret.Sale = sale;
            return Dto(ret);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var again = await db.Returns.AsNoTracking().Include(x => x.Refunds).Include(x => x.Sale).FirstOrDefaultAsync(x => x.OperationKey == opKey);
            if (again is not null) return Dto(again);
            throw;
        }
    }

    static void AddReturnLine(SaleReturn ret, SaleLine l, decimal qty)
    {
        // Proporcional al total de la línea (conserva precio, descuento e impuesto históricos).
        var full = qty == l.Quantity;
        var total = full ? l.LineTotal : Money.R(l.LineTotal * qty / l.Quantity);
        var net = full ? l.NetAmount : Money.R(l.NetAmount * qty / l.Quantity);
        ret.Lines.Add(new ReturnLine { SaleLineId = l.Id, ProductId = l.ProductId, Quantity = qty, NetAmount = net, TaxAmount = total - net, LineTotal = total });
    }

    static ReturnDto Dto(SaleReturn r) => new(r.Id, r.Number, r.Sale?.Number ?? "", r.CreatedAt, r.Reason, r.IsVoid, r.Total, r.Refunds.Select(x => (x.Method, x.Amount)).ToList());
}
