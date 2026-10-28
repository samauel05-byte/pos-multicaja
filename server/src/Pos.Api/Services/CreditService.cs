using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record CustomerPaymentReq(Guid OperationKey, Guid CustomerId, Guid? RegisterId, PayMethod Method, decimal Amount, string? Reference, Guid? SaleId);
public record CustomerPaymentDto(Guid Id, string Number, decimal Amount, PayMethod Method, decimal NewBalance, bool Duplicate);

public class CreditService(AppDb db, CurrentUser user, AuditService audit, Numbering numbering)
{
    public async Task<CustomerPaymentDto> PayAsync(CustomerPaymentReq r)
    {
        if (r.OperationKey == Guid.Empty) throw ApiException.Bad("operation_key_required", "Falta la clave de operación.");
        if (r.Amount <= 0) throw ApiException.Bad("invalid_amount", "El abono debe ser mayor que cero.");
        if (r.Method == PayMethod.Credit) throw ApiException.Bad("invalid_method", "Un abono no puede pagarse con crédito.");
        var amount = Money.R(r.Amount);

        var dup = await db.CustomerPayments.AsNoTracking().Include(x => x.Customer).FirstOrDefaultAsync(x => x.OperationKey == r.OperationKey);
        if (dup is not null) return new CustomerPaymentDto(dup.Id, dup.Number, dup.Amount, dup.Method, dup.Customer!.Balance, true);

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            Shift? shift = null;
            if (r.Method == PayMethod.Cash)
            {
                // El efectivo debe entrar a un turno abierto para que cuadre la caja.
                if (r.RegisterId is null) throw ApiException.Bad("register_required", "Indique la caja que recibe el efectivo.");
                shift = await db.Shifts.FromSqlInterpolated($"SELECT * FROM \"Shifts\" WHERE \"RegisterId\" = {r.RegisterId} AND \"Status\" = 1 FOR SHARE").FirstOrDefaultAsync()
                    ?? throw ApiException.Conflict("no_open_shift", "La caja no tiene un turno abierto.");
            }
            var c = await db.Customers.FromSqlInterpolated($"SELECT * FROM \"Customers\" WHERE \"Id\" = {r.CustomerId} FOR UPDATE").FirstOrDefaultAsync()
                ?? throw ApiException.NotFound("Cliente no encontrado.");
            if (amount > c.Balance) throw ApiException.Conflict("overpayment", $"El abono excede el saldo del cliente ({c.Balance:0.00}).");

            // Aplica a la venta indicada o, si no, a las más antiguas (FIFO).
            var q = db.Sales.FromSqlInterpolated($"SELECT * FROM \"Sales\" WHERE \"CustomerId\" = {c.Id} AND \"CreditBalance\" > 0 AND \"Status\" = 1 ORDER BY \"CreatedAt\" FOR UPDATE");
            var open = await q.ToListAsync();
            if (r.SaleId is not null) open = open.Where(s => s.Id == r.SaleId).ToList();
            var pay = new CustomerPayment { OperationKey = r.OperationKey, CustomerId = c.Id, ShiftId = shift?.Id, UserId = user.Id, Method = r.Method, Amount = amount, Reference = r.Reference };
            var left = amount;
            foreach (var s in open)
            {
                if (left <= 0) break;
                var a = Math.Min(left, s.CreditBalance);
                s.CreditBalance -= a;
                left -= a;
                pay.Allocations.Add(new CustomerPaymentAllocation { SaleId = s.Id, Amount = a });
            }
            if (left > 0) throw ApiException.Conflict("overpayment", "El abono excede el saldo de la venta seleccionada.");
            c.Balance -= amount;
            pay.Number = await numbering.NextAsync("cpay", "AB");
            db.CustomerPayments.Add(pay);
            await db.SaveChangesAsync();
            if (shift is not null)
                db.CashMovements.Add(new CashMovement { ShiftId = shift.Id, Type = CashMovementType.CustomerPaymentCash, Amount = amount, DocumentId = pay.Id, Reason = pay.Number, UserId = user.Id });
            audit.Log("customer.payment", "customer", c.Id, $"{pay.Number} {amount} {r.Method}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return new CustomerPaymentDto(pay.Id, pay.Number, amount, r.Method, c.Balance, false);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var again = await db.CustomerPayments.AsNoTracking().Include(x => x.Customer).FirstOrDefaultAsync(x => x.OperationKey == r.OperationKey);
            if (again is not null) return new CustomerPaymentDto(again.Id, again.Number, again.Amount, again.Method, again.Customer!.Balance, true);
            throw;
        }
    }
}
