using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public record PreviewReq(List<SaleLineReq> Lines);
public record PrintResultReq(bool Ok, string? Error);
public record QuoteReq(Guid? CustomerId, int ValidDays, string? Notes, List<SaleLineReq> Lines);

public static class SalesEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization();

        // ---- Turnos y caja ----
        g.MapPost("/shifts/open", async (OpenShiftRequest r, ShiftService svc) => { var s = await svc.OpenAsync(r); return Results.Ok(await svc.SummaryAsync(s.Id)); });
        g.MapPost("/shifts/movement", async (CashMoveRequest r, ShiftService svc) => { await svc.AddMovementAsync(r); return Results.Ok(await svc.SummaryAsync(r.ShiftId)); });
        g.MapPost("/shifts/close", async (CloseShiftRequest r, ShiftService svc) => Results.Ok(await svc.CloseAsync(r)));
        g.MapGet("/shifts/{id:guid}/summary", async (Guid id, ShiftService svc, AppDb db, CurrentUser cu) =>
        {
            var s = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Turno no encontrado.");
            if (cu.Role == Role.Cashier && s.OpenedByUserId != cu.Id) throw ApiException.Forbidden();
            return await svc.SummaryAsync(id);
        });
        g.MapGet("/shifts/{id:guid}/movements", async (Guid id, AppDb db, CurrentUser cu) =>
        {
            var s = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Turno no encontrado.");
            if (cu.Role == Role.Cashier && s.OpenedByUserId != cu.Id) throw ApiException.Forbidden();
            return await db.CashMovements.AsNoTracking().Where(m => m.ShiftId == id).OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.CreatedAt, Type = m.Type.ToString(), m.Amount, m.Reason }).ToListAsync();
        });
        g.MapGet("/shifts", async (AppDb db, CurrentUser cu, DateTime? from, DateTime? to, Guid? registerId) =>
        {
            cu.Require(Perm.ReportsView);
            var q = db.Shifts.AsNoTracking().AsQueryable();
            if (registerId is not null) q = q.Where(s => s.RegisterId == registerId);
            if (from is not null) q = q.Where(s => s.OpenedAt >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(s => s.OpenedAt < to.Value.ToUniversalTime().AddDays(1));
            return await q.OrderByDescending(s => s.OpenedAt).Take(200).Select(s => new
            {
                s.Id, Register = s.Register!.Name, OpenedBy = s.OpenedBy!.FullName, s.OpenedAt, s.ClosedAt, Status = s.Status.ToString(),
                s.OpeningFloat, s.ExpectedCash, s.CountedCash, s.CashDifference, s.CardTotal, s.TransferTotal, s.CreditTotal, s.CloseNotes,
            }).ToListAsync();
        });

        // ---- Ventas ----
        g.MapPost("/sales/preview", async (PreviewReq r, AppDb db) =>
        {
            var company = await db.Companies.AsNoTracking().FirstAsync();
            var ids = r.Lines.Select(l => l.ProductId).Distinct().ToList();
            var prods = await db.Products.AsNoTracking().Include(p => p.TaxRate).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            var lines = r.Lines.Select(l =>
            {
                var p = prods.GetValueOrDefault(l.ProductId) ?? throw ApiException.Bad("product_unavailable", "Producto no encontrado.");
                var c = Pricing.Line(Money.Q(l.Quantity), p.Price, l.DiscountPercent, l.DiscountAmount, p.TaxRate!.Rate, company.PricesIncludeTax);
                return new { l.ProductId, l.Quantity, UnitPrice = p.Price, DiscountPercent = c.EffectivePercent, DiscountAmount = c.Discount, NetAmount = c.Net, TaxAmount = c.Tax, LineTotal = c.Total };
            }).ToList();
            return new { lines, subtotal = lines.Sum(x => x.NetAmount), discountTotal = lines.Sum(x => x.DiscountAmount), taxTotal = lines.Sum(x => x.TaxAmount), total = lines.Sum(x => x.LineTotal) };
        });

        g.MapPost("/sales", async (CreateSaleReq r, SaleService svc) =>
        {
            var (sale, dup) = await svc.CreateAsync(r);
            return Results.Json(new { sale, duplicate = dup }, statusCode: dup ? 200 : 201);
        });

        // Tras un corte de red, el cliente consulta aquí antes de reintentar el cobro.
        g.MapGet("/sales/by-operation/{key:guid}", async (Guid key, SaleService svc) =>
        {
            var s = await svc.FindByOperationAsync(key);
            return s is null ? Results.Json(new { exists = false }) : Results.Json(new { exists = true, sale = s });
        });

        g.MapGet("/sales/{id:guid}", async (Guid id, SaleService svc, CurrentUser cu, AppDb db) =>
        {
            var s = await svc.GetDtoAsync(id);
            var owner = await db.Sales.AsNoTracking().Where(x => x.Id == id).Select(x => x.UserId).FirstAsync();
            if (cu.Role == Role.Cashier && owner != cu.Id) throw ApiException.Forbidden();
            return s;
        });

        g.MapGet("/sales", async (AppDb db, CurrentUser cu, DateTime? from, DateTime? to, Guid? registerId, Guid? userId, string? number, SaleStatus? status, int? limit) =>
        {
            var q = db.Sales.AsNoTracking().AsQueryable();
            if (cu.Role == Role.Cashier) q = q.Where(s => s.UserId == cu.Id); else if (userId is not null) q = q.Where(s => s.UserId == userId);
            if (registerId is not null) q = q.Where(s => s.RegisterId == registerId);
            if (status is not null) q = q.Where(s => s.Status == status);
            if (!string.IsNullOrWhiteSpace(number)) q = q.Where(s => s.Number.Contains(number.Trim().ToUpper()));
            if (from is not null) q = q.Where(s => s.CreatedAt >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(s => s.CreatedAt < to.Value.ToUniversalTime().AddDays(1));
            return await q.OrderByDescending(s => s.CreatedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).Select(s => new
            {
                s.Id, s.Number, s.CreatedAt, Status = s.Status.ToString(), s.Total, s.CreditAmount, Customer = s.Customer!.Name, Cashier = s.User!.FullName, s.PrintCount,
            }).ToListAsync();
        });

        // ---- Tickets ----
        g.MapPost("/sales/{id:guid}/ticket", async (Guid id, TicketService svc, AppDb db, CurrentUser cu, bool? reprint, int? columns) =>
        {
            var owner = await db.Sales.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.UserId).FirstOrDefaultAsync() ?? throw ApiException.NotFound("Venta no encontrada.");
            if (cu.Role == Role.Cashier && owner != cu.Id) throw ApiException.Forbidden("Solo puede reimprimir sus propias ventas.");
            return await svc.BuildAsync(id, reprint ?? false, columns is 32 or 42 or 48 ? columns.Value : 42);
        });
        g.MapPost("/print-jobs/{id:guid}/result", async (Guid id, PrintResultReq r, TicketService svc) => { await svc.MarkAsync(id, r.Ok, r.Error); return Results.Ok(); });

        // ---- Devoluciones y anulaciones ----
        g.MapPost("/returns", async (CreateReturnReq r, ReturnService svc) => Results.Ok(await svc.ReturnAsync(r)));
        g.MapPost("/sales/{id:guid}/void", async (Guid id, VoidSaleReq r, ReturnService svc) => Results.Ok(await svc.VoidAsync(id, r)));
        g.MapGet("/returns", async (AppDb db, CurrentUser cu, DateTime? from, DateTime? to) =>
        {
            cu.Require(Perm.ReportsView);
            var q = db.Returns.AsNoTracking().AsQueryable();
            if (from is not null) q = q.Where(s => s.CreatedAt >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(s => s.CreatedAt < to.Value.ToUniversalTime().AddDays(1));
            return await q.OrderByDescending(r => r.CreatedAt).Take(200).Select(r => new { r.Id, r.Number, SaleNumber = r.Sale!.Number, r.CreatedAt, r.Reason, r.IsVoid, r.Total }).ToListAsync();
        });

        // ---- Cotizaciones ----
        g.MapPost("/quotes", async (QuoteReq r, AppDb db, CurrentUser cu, Numbering num, AuditService audit) =>
        {
            if (r.Lines is not { Count: > 0 }) throw ApiException.Bad("empty_quote", "Agregue al menos un producto.");
            var company = await db.Companies.AsNoTracking().FirstAsync();
            var ids = r.Lines.Select(l => l.ProductId).Distinct().ToList();
            var prods = await db.Products.AsNoTracking().Include(p => p.TaxRate).Where(p => ids.Contains(p.Id) && p.Active).ToDictionaryAsync(p => p.Id);
            await using var tx = await db.Database.BeginTransactionAsync();
            var q = new Quote { UserId = cu.Id, CustomerId = r.CustomerId, Notes = r.Notes, ValidUntil = DateTime.UtcNow.AddDays(Math.Clamp(r.ValidDays, 1, 90)) };
            foreach (var l in r.Lines)
            {
                var p = prods.GetValueOrDefault(l.ProductId) ?? throw ApiException.Bad("product_unavailable", "Producto no disponible.");
                if (l.Quantity <= 0) throw ApiException.Bad("invalid_quantity", "Cantidad inválida.");
                var c = Pricing.Line(Money.Q(l.Quantity), p.Price, l.DiscountPercent, l.DiscountAmount, p.TaxRate!.Rate, company.PricesIncludeTax);
                q.Lines.Add(new QuoteLine { ProductId = p.Id, ProductName = p.Name, Quantity = Money.Q(l.Quantity), UnitPrice = p.Price, DiscountPercent = c.EffectivePercent,
                    DiscountAmount = c.Discount, TaxRate = p.TaxRate.Rate, NetAmount = c.Net, TaxAmount = c.Tax, LineTotal = c.Total });
            }
            q.DiscountTotal = q.Lines.Sum(x => x.DiscountAmount); q.Subtotal = q.Lines.Sum(x => x.NetAmount);
            q.TaxTotal = q.Lines.Sum(x => x.TaxAmount); q.Total = q.Lines.Sum(x => x.LineTotal);
            q.Number = await num.NextAsync("quote", "COT");
            db.Quotes.Add(q);
            audit.Log("quote.create", "quote", q.Id, q.Number);
            await db.SaveChangesAsync(); await tx.CommitAsync();
            return Results.Created($"/api/quotes/{q.Id}", new { q.Id, q.Number, q.Total });
        });

        g.MapGet("/quotes", async (AppDb db, QuoteStatus? status) =>
        {
            var now = DateTime.UtcNow;
            var q = db.Quotes.AsNoTracking().AsQueryable();
            if (status is not null) q = q.Where(x => x.Status == status);
            return await q.OrderByDescending(x => x.CreatedAt).Take(200).Select(x => new
            {
                x.Id, x.Number, x.CreatedAt, x.ValidUntil, Customer = x.Customer!.Name, x.Total,
                Status = (x.Status == QuoteStatus.Open && x.ValidUntil < now ? QuoteStatus.Expired : x.Status).ToString(),
            }).ToListAsync();
        });

        g.MapGet("/quotes/{id:guid}", async (Guid id, AppDb db) =>
        {
            var q = await db.Quotes.AsNoTracking().Include(x => x.Lines).Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Cotización no encontrada.");
            var expired = q.Status == QuoteStatus.Open && q.ValidUntil < DateTime.UtcNow;
            return new { q.Id, q.Number, q.CreatedAt, q.ValidUntil, q.CustomerId, Customer = q.Customer?.Name, q.Notes, Status = (expired ? QuoteStatus.Expired : q.Status).ToString(),
                q.Subtotal, q.DiscountTotal, q.TaxTotal, q.Total, q.SaleId, Lines = q.Lines.Select(l => new { l.ProductId, l.ProductName, l.Quantity, l.UnitPrice, l.DiscountPercent, l.LineTotal }) };
        });

        g.MapPost("/quotes/{id:guid}/cancel", async (Guid id, AppDb db, AuditService audit) =>
        {
            var q = await db.Quotes.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Cotización no encontrada.");
            if (q.Status != QuoteStatus.Open) throw ApiException.Conflict("quote_not_open", "La cotización no está abierta.");
            q.Status = QuoteStatus.Cancelled; audit.Log("quote.cancel", "quote", q.Id, q.Number); await db.SaveChangesAsync(); return Results.Ok();
        });
    }
}
