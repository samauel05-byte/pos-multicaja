using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record ShiftSummary(
    Guid ShiftId, Guid RegisterId, string RegisterName, string OpenedBy, DateTime OpenedAt, DateTime? ClosedAt, ShiftStatus Status,
    decimal OpeningFloat, decimal CashSales, decimal CashIn, decimal CashOut, decimal CashRefunds, decimal CashPayments,
    decimal ExpectedCash, decimal CardSales, decimal TransferSales, decimal CreditSales,
    decimal CardRefunds, decimal TransferRefunds, decimal CreditReductions,
    int SalesCount, decimal SalesTotal, int VoidedCount,
    decimal? CountedCash, decimal? CashDifference, string? CloseNotes);

public record OpenShiftRequest(Guid RegisterId, decimal OpeningFloat);
public record CashMoveRequest(Guid ShiftId, decimal Amount, string Reason, bool IsOut);
public record CloseShiftRequest(Guid ShiftId, decimal CountedCash, string? Notes, Dictionary<string, int>? Denominations);

public class ShiftService(AppDb db, CurrentUser user, AuditService audit)
{
    public async Task<Shift> OpenAsync(OpenShiftRequest r)
    {
        if (r.OpeningFloat < 0) throw ApiException.Bad("invalid_float", "El fondo inicial no puede ser negativo.");
        var reg = await db.Registers.FirstOrDefaultAsync(x => x.Id == r.RegisterId && x.Active) ?? throw ApiException.NotFound("Caja no encontrada.");
        var open = await db.Shifts.AnyAsync(s => s.RegisterId == reg.Id && s.Status == ShiftStatus.Open);
        if (open) throw ApiException.Conflict("shift_already_open", "Esta caja ya tiene un turno abierto.");

        var shift = new Shift { RegisterId = reg.Id, OpenedByUserId = user.Id, OpeningFloat = Money.R(r.OpeningFloat) };
        db.Shifts.Add(shift);
        db.CashMovements.Add(new CashMovement { ShiftId = shift.Id, Type = CashMovementType.Opening, Amount = shift.OpeningFloat, Reason = "Fondo inicial", UserId = user.Id });
        audit.Log("shift.open", "shift", shift.Id, $"caja={reg.Name} fondo={shift.OpeningFloat}");
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        { throw ApiException.Conflict("shift_already_open", "Esta caja ya tiene un turno abierto."); } // carrera: la BD lo garantiza
        return shift;
    }

    public async Task<Shift> RequireOpenForSaleAsync(Guid registerId)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.RegisterId == registerId && s.Status == ShiftStatus.Open)
            ?? throw ApiException.Conflict("no_open_shift", "La caja no tiene un turno abierto. Abra la caja antes de cobrar.");
        if (user.Role == Role.Cashier && shift.OpenedByUserId != user.Id)
            throw ApiException.Forbidden("El turno de esta caja pertenece a otro cajero.");
        return shift;
    }

    public async Task AddMovementAsync(CashMoveRequest r)
    {
        var s = await db.Shifts.FirstOrDefaultAsync(x => x.Id == r.ShiftId) ?? throw ApiException.NotFound("Turno no encontrado.");
        if (s.Status != ShiftStatus.Open) throw ApiException.Conflict("shift_closed", "El turno está cerrado.");
        if (user.Role == Role.Cashier && s.OpenedByUserId != user.Id) throw ApiException.Forbidden();
        if (r.Amount <= 0) throw ApiException.Bad("invalid_amount", "El monto debe ser mayor que cero.");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw ApiException.Bad("reason_required", "Indique el motivo del movimiento.");
        var amount = Money.R(r.Amount);
        if (r.IsOut)
        {
            var summary = await SummaryAsync(s.Id);
            if (summary.ExpectedCash < amount) throw ApiException.Conflict("insufficient_cash", "La salida supera el efectivo esperado en caja.");
        }
        var m = new CashMovement
        {
            ShiftId = s.Id, Type = r.IsOut ? CashMovementType.CashOut : CashMovementType.CashIn,
            Amount = r.IsOut ? -amount : amount, Reason = r.Reason.Trim(), UserId = user.Id,
        };
        db.CashMovements.Add(m);
        audit.Log(r.IsOut ? "cash.out" : "cash.in", "shift", s.Id, $"{amount} {r.Reason}");
        await db.SaveChangesAsync();
    }

    public async Task<ShiftSummary> SummaryAsync(Guid shiftId)
    {
        var s = await db.Shifts.AsNoTracking().Include(x => x.Register).Include(x => x.OpenedBy).FirstOrDefaultAsync(x => x.Id == shiftId)
            ?? throw ApiException.NotFound("Turno no encontrado.");
        var moves = await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shiftId).ToListAsync();
        decimal Sum(CashMovementType t) => moves.Where(m => m.Type == t).Sum(m => m.Amount);

        var sales = await db.Sales.AsNoTracking().Where(x => x.ShiftId == shiftId).Include(x => x.Payments).ToListAsync();
        var valid = sales.Where(x => x.Status == SaleStatus.Completed).ToList();
        decimal ByMethod(PayMethod pm) => sales.SelectMany(x => x.Payments).Where(p => p.Method == pm).Sum(p => p.Amount);
        var rets = await db.Returns.AsNoTracking().Where(x => x.ShiftId == shiftId).Include(x => x.Refunds).ToListAsync();
        decimal RefundBy(PayMethod pm) => rets.SelectMany(x => x.Refunds).Where(x => x.Method == pm).Sum(x => x.Amount);

        // Abonos recibidos con tarjeta/transferencia en este turno cuentan en su medio.
        var abonos = await db.CustomerPayments.AsNoTracking().Where(x => x.ShiftId == shiftId).ToListAsync();

        var cashSales = Sum(CashMovementType.SaleCash);
        var cashPay = Sum(CashMovementType.CustomerPaymentCash);
        var refunds = -Sum(CashMovementType.RefundCash);
        var cashIn = Sum(CashMovementType.CashIn);
        var cashOut = -Sum(CashMovementType.CashOut);
        var expected = moves.Sum(m => m.Amount);

        return new ShiftSummary(
            s.Id, s.RegisterId, s.Register!.Name, s.OpenedBy!.FullName, s.OpenedAt, s.ClosedAt, s.Status,
            s.OpeningFloat, cashSales, cashIn, cashOut, refunds, cashPay, expected,
            ByMethod(PayMethod.Card) + abonos.Where(a => a.Method == PayMethod.Card).Sum(a => a.Amount) - RefundBy(PayMethod.Card),
            ByMethod(PayMethod.Transfer) + abonos.Where(a => a.Method == PayMethod.Transfer).Sum(a => a.Amount) - RefundBy(PayMethod.Transfer),
            ByMethod(PayMethod.Credit) - RefundBy(PayMethod.Credit),
            RefundBy(PayMethod.Card), RefundBy(PayMethod.Transfer), RefundBy(PayMethod.Credit),
            valid.Count, valid.Sum(x => x.Total), sales.Count(x => x.Status == SaleStatus.Voided),
            s.CountedCash, s.CashDifference, s.CloseNotes);
    }

    public async Task<ShiftSummary> CloseAsync(CloseShiftRequest r)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var s = await db.Shifts.FromSqlInterpolated($"SELECT * FROM \"Shifts\" WHERE \"Id\" = {r.ShiftId} FOR UPDATE").FirstOrDefaultAsync()
            ?? throw ApiException.NotFound("Turno no encontrado.");
        if (s.Status != ShiftStatus.Open) throw ApiException.Conflict("shift_closed", "El turno ya está cerrado.");
        if (s.OpenedByUserId != user.Id && !user.Can(Perm.ShiftsCloseAny)) throw ApiException.Forbidden("Solo el cajero del turno o un supervisor puede cerrarlo.");
        if (r.CountedCash < 0) throw ApiException.Bad("invalid_amount", "El efectivo contado no puede ser negativo.");

        var sum = await SummaryAsync(s.Id);
        var counted = Money.R(r.CountedCash);
        var diff = counted - sum.ExpectedCash;
        var company = await db.Companies.AsNoTracking().FirstAsync();
        if (Math.Abs(diff) > company.CashDifferenceToleranceAmount && string.IsNullOrWhiteSpace(r.Notes))
            throw ApiException.Bad("notes_required", $"La diferencia ({diff:0.00}) supera la tolerancia; indique una nota explicativa.");

        s.Status = ShiftStatus.Closed;
        s.ClosedAt = DateTime.UtcNow;
        s.ClosedByUserId = user.Id;
        s.ExpectedCash = sum.ExpectedCash;
        s.CountedCash = counted;
        s.CashDifference = diff;
        s.CardTotal = sum.CardSales;
        s.TransferTotal = sum.TransferSales;
        s.CreditTotal = sum.CreditSales;
        s.CloseNotes = r.Notes?.Trim();
        s.CountDetailJson = r.Denominations is null ? null : System.Text.Json.JsonSerializer.Serialize(r.Denominations);
        audit.Log("shift.close", "shift", s.Id, $"esperado={sum.ExpectedCash} contado={counted} dif={diff}");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await SummaryAsync(s.Id);
    }
}
