using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public static class ReportEndpoints
{
    // Las fechas llegan como yyyy-MM-dd y se interpretan en la zona horaria del servidor (TZ del sistema).
    static (DateTime From, DateTime To) Range(string? from, string? to)
    {
        var tz = TimeZoneInfo.Local;
        var f = DateTime.TryParse(from, out var a) ? a.Date : DateTime.Today.AddDays(-30);
        var t = DateTime.TryParse(to, out var b) ? b.Date.AddDays(1) : DateTime.Today.AddDays(1);
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(f, DateTimeKind.Unspecified), tz), TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), tz));
    }

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/reports").RequireAuthorization();

        g.MapGet("/sales", async (AppDb db, CurrentUser cu, string? from, string? to, string? groupBy, Guid? registerId, Guid? userId) =>
        {
            cu.Require(Perm.ReportsView);
            var (f, t) = Range(from, to);
            var sales = db.Sales.AsNoTracking().Where(s => s.CreatedAt >= f && s.CreatedAt < t && s.Status == SaleStatus.Completed);
            if (registerId is not null) sales = sales.Where(s => s.RegisterId == registerId);
            if (userId is not null) sales = sales.Where(s => s.UserId == userId);
            var tz = TimeZoneInfo.Local;
            var list = await sales.Select(s => new { s.CreatedAt, s.Total, s.TaxTotal, s.DiscountTotal, s.RegisterId, s.UserId, Cashier = s.User!.FullName, s.CreditAmount,
                Pay = s.Payments.Select(p => new { p.Method, p.Amount }) }).ToListAsync();
            var regs = await db.Registers.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
            var rets = await db.Returns.AsNoTracking().Where(r => r.CreatedAt >= f && r.CreatedAt < t).Include(r => r.Refunds).ToListAsync();

            object Row(string key, IEnumerable<dynamic> items) => new { key, count = items.Count(), total = items.Sum(x => (decimal)x.Total), tax = items.Sum(x => (decimal)x.TaxTotal), discount = items.Sum(x => (decimal)x.DiscountTotal) };
            var rows = (groupBy ?? "day") switch
            {
                "register" => list.GroupBy(x => regs.GetValueOrDefault(x.RegisterId, "?")).Select(gr => Row(gr.Key, gr)).ToList(),
                "user" => list.GroupBy(x => x.Cashier).Select(gr => Row(gr.Key, gr)).ToList(),
                "method" => list.SelectMany(x => x.Pay.Select(p => new { p.Method, p.Amount })).GroupBy(p => p.Method.ToString())
                    .Select(gr => (object)new { key = gr.Key, count = gr.Count(), total = gr.Sum(p => p.Amount), tax = 0m, discount = 0m }).ToList(),
                _ => list.GroupBy(x => TimeZoneInfo.ConvertTimeFromUtc(x.CreatedAt, tz).ToString("yyyy-MM-dd")).OrderBy(gr => gr.Key).Select(gr => Row(gr.Key, gr)).ToList(),
            };
            return new
            {
                rows,
                totals = new { count = list.Count, sales = list.Sum(x => x.Total), tax = list.Sum(x => x.TaxTotal), discount = list.Sum(x => x.DiscountTotal),
                    returns = rets.Where(r => !r.IsVoid).Sum(r => r.Total), voids = rets.Where(r => r.IsVoid).Sum(r => r.Total) },
            };
        });

        g.MapGet("/low-stock", async (AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.ReportsView);
            var rows = await db.Products.AsNoTracking().Where(p => p.Active && p.TracksStock)
                .Select(p => new { p.Id, p.Sku, p.Name, p.MinStock, Stock = db.StockLevels.Where(s => s.ProductId == p.Id).Sum(s => (decimal?)s.Quantity) ?? 0 }).ToListAsync();
            return rows.Where(r => r.Stock <= r.MinStock).OrderBy(r => r.Stock - r.MinStock);
        });

        g.MapGet("/receivables", async (AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.ReportsView);
            var now = DateTime.UtcNow;
            var open = await db.Sales.AsNoTracking().Where(s => s.CreditBalance > 0 && s.Status == SaleStatus.Completed)
                .Select(s => new { s.Number, s.CreatedAt, s.DueDate, s.CreditBalance, Customer = s.Customer!.Name, s.CustomerId }).ToListAsync();
            return open.OrderBy(x => x.DueDate).Select(x => new
            { x.Number, x.CreatedAt, x.DueDate, x.CreditBalance, x.Customer, DaysOverdue = x.DueDate < now ? (int)(now - x.DueDate!.Value).TotalDays : 0 });
        });

        g.MapGet("/payables", async (AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.ReportsView);
            var now = DateTime.UtcNow;
            var rows = await db.Purchases.AsNoTracking().Where(p => p.Total > p.Paid)
                .Select(p => new { p.Number, p.ReceivedAt, p.DueDate, Supplier = p.Supplier!.Name, Balance = p.Total - p.Paid }).ToListAsync();
            return rows.OrderBy(x => x.DueDate).Select(x => new { x.Number, x.ReceivedAt, x.DueDate, x.Supplier, x.Balance, DaysOverdue = x.DueDate < now ? (int)(now - x.DueDate).TotalDays : 0 });
        });
    }
}
