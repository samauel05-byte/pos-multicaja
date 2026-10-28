using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public record ProductReq(string Sku, string? Barcode, string Name, Guid? CategoryId, Guid UnitId, Guid TaxRateId, decimal Cost, decimal Price,
    decimal MinStock, bool TracksStock, bool Active, decimal? InitialStock, Guid? InitialLocationId);
public record NamedReq(string Name);
public record UnitReq(string Name, string Abbreviation, bool AllowsDecimals);
public record TaxReq(string Name, decimal Rate, bool Active);

public static class CatalogEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization();

        g.MapGet("/bootstrap", async (AppDb db, CurrentUser cu) =>
        {
            var company = await db.Companies.AsNoTracking().FirstAsync();
            return new
            {
                company = new { company.Name, company.TaxId, company.CurrencyCode, company.CurrencySymbol, company.PricesIncludeTax, company.AllowNegativeStock,
                    company.CashierMaxDiscountPercent, company.SupervisorMaxDiscountPercent, company.CashDifferenceToleranceAmount, company.TicketFooter, company.FiscalModuleEnabled },
                branches = await db.Branches.AsNoTracking().Where(b => b.Active).ToListAsync(),
                locations = await db.Locations.AsNoTracking().Where(l => l.Active).ToListAsync(),
                registers = await db.Registers.AsNoTracking().Where(r => r.Active).ToListAsync(),
                categories = await db.Categories.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Name).ToListAsync(),
                units = await db.Units.AsNoTracking().OrderBy(c => c.Name).ToListAsync(),
                taxRates = await db.TaxRates.AsNoTracking().Where(t => t.Active).OrderBy(c => c.Name).ToListAsync(),
                openShifts = await db.Shifts.AsNoTracking().Where(s => s.Status == ShiftStatus.Open).Select(s => new { s.Id, s.RegisterId, s.OpenedByUserId, s.OpenedAt, s.OpeningFloat }).ToListAsync(),
            };
        });

        g.MapGet("/products", async (AppDb db, CurrentUser cu, string? q, Guid? categoryId, bool? active, Guid? locationId, int? limit, bool? lowStock) =>
        {
            var query = db.Products.AsNoTracking().AsQueryable();
            if (active is not null) query = query.Where(p => p.Active == active);
            if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = q.Trim();
                query = query.Where(p => p.Barcode == t || p.Sku == t || EF.Functions.ILike(p.Name, $"%{t}%") || EF.Functions.ILike(p.Sku, $"%{t}%"));
            }
            var showCost = cu.Can(Perm.ProductsManage);
            var rows = await query.OrderBy(p => p.Name).Take(Math.Clamp(limit ?? 60, 1, 500))
                .Select(p => new
                {
                    p.Id, p.Sku, p.Barcode, p.Name, p.CategoryId, CategoryName = p.Category!.Name, p.UnitId, UnitAbbr = p.Unit!.Abbreviation, AllowsDecimals = p.Unit.AllowsDecimals,
                    p.TaxRateId, TaxRate = p.TaxRate!.Rate, Cost = showCost ? p.Cost : (decimal?)null, p.Price, p.MinStock, p.TracksStock, p.Active,
                    Stock = db.StockLevels.Where(s => s.ProductId == p.Id && (locationId == null || s.LocationId == locationId)).Sum(s => (decimal?)s.Quantity) ?? 0,
                }).ToListAsync();
            if (lowStock == true) rows = rows.Where(r => r.TracksStock && r.Stock <= r.MinStock).ToList();
            return rows;
        });

        g.MapPost("/products", async (ProductReq r, AppDb db, CurrentUser cu, AuditService audit, StockService stock) =>
        {
            cu.Require(Perm.ProductsManage);
            Validate(r);
            await using var tx = await db.Database.BeginTransactionAsync();
            var p = new Product();
            Apply(p, r);
            db.Products.Add(p);
            audit.Log("product.create", "product", p.Id, $"{p.Sku} {p.Name} precio={p.Price}");
            await db.SaveChangesAsync();
            if (r.InitialStock is > 0 && r.InitialLocationId is not null && p.TracksStock)
            {
                var bal = await stock.IncreaseAsync(p.Id, r.InitialLocationId.Value, r.InitialStock.Value);
                stock.Kardex(p.Id, r.InitialLocationId.Value, MovementType.Initial, r.InitialStock.Value, bal, p.Cost, "INICIAL", null, "Existencia inicial", cu.Id);
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
            return Results.Created($"/api/products/{p.Id}", new { p.Id });
        });

        g.MapPut("/products/{id:guid}", async (Guid id, ProductReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.ProductsManage);
            Validate(r);
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Producto no encontrado.");
            var before = $"precio={p.Price} costo={p.Cost} activo={p.Active}";
            Apply(p, r);   // los documentos históricos conservan su propio precio/impuesto copiado
            audit.Log("product.update", "product", p.Id, $"{before} → precio={p.Price} costo={p.Cost} activo={p.Active}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Retirar del catálogo conserva las referencias de ventas e inventario.
        g.MapDelete("/products/{id:guid}", async (Guid id, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.ProductsManage);
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Producto no encontrado.");
            if (p.Active)
            {
                p.Active = false;
                audit.Log("product.delete", "product", p.Id, $"{p.Sku} {p.Name}: retirado del catálogo; historial conservado");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { p.Id, p.Active });
        });

        g.MapPost("/categories", async (NamedReq r, AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.ProductsManage);
            if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.Bad("name_required", "Nombre requerido.");
            var c = new Category { Name = r.Name.Trim() }; db.Categories.Add(c); await db.SaveChangesAsync(); return new { c.Id, c.Name };
        });
        g.MapPost("/units", async (UnitReq r, AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.ProductsManage);
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Abbreviation)) throw ApiException.Bad("name_required", "Nombre y abreviatura requeridos.");
            var u = new Unit { Name = r.Name.Trim(), Abbreviation = r.Abbreviation.Trim(), AllowsDecimals = r.AllowsDecimals }; db.Units.Add(u); await db.SaveChangesAsync(); return new { u.Id };
        });
        g.MapPost("/tax-rates", async (TaxReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.SettingsManage);
            if (r.Rate < 0 || r.Rate > 1) throw ApiException.Bad("invalid_rate", "La tasa debe estar entre 0 y 1 (ej. 0.18).");
            var t = new TaxRate { Name = r.Name.Trim(), Rate = r.Rate, Active = r.Active }; db.TaxRates.Add(t);
            audit.Log("taxrate.create", "taxrate", t.Id, $"{t.Name} {t.Rate}");
            await db.SaveChangesAsync(); return new { t.Id };
        });

        // ---- Inventario ----
        g.MapGet("/inventory", async (AppDb db, CurrentUser cu, Guid? locationId, string? q) =>
        {
            cu.Require(Perm.InventoryView);
            var qry = from s in db.StockLevels.AsNoTracking()
                      join p in db.Products.AsNoTracking() on s.ProductId equals p.Id
                      join l in db.Locations.AsNoTracking() on s.LocationId equals l.Id
                      where (locationId == null || s.LocationId == locationId) && (q == null || EF.Functions.ILike(p.Name, $"%{q}%") || p.Sku == q || p.Barcode == q)
                      orderby p.Name
                      select new { p.Id, p.Sku, p.Name, p.MinStock, LocationId = l.Id, Location = l.Name, s.Quantity, Low = p.TracksStock && s.Quantity <= p.MinStock };
            return await qry.Take(500).ToListAsync();
        });

        g.MapGet("/inventory/kardex/{productId:guid}", async (Guid productId, AppDb db, CurrentUser cu, Guid? locationId, DateTime? from, DateTime? to) =>
        {
            cu.Require(Perm.InventoryView);
            var q = db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == productId);
            if (locationId is not null) q = q.Where(m => m.LocationId == locationId);
            if (from is not null) q = q.Where(m => m.CreatedAt >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(m => m.CreatedAt < to.Value.ToUniversalTime().AddDays(1));
            return await q.OrderByDescending(m => m.CreatedAt).Take(500)
                .Select(m => new { m.Id, m.CreatedAt, Type = m.Type.ToString(), m.Quantity, m.BalanceAfter, m.UnitCost, m.Reference, m.Reason, m.LocationId }).ToListAsync();
        });

        g.MapPost("/inventory/adjust", async (AdjustReq r, PurchaseService svc, CurrentUser cu) =>
        {
            cu.Require(Perm.InventoryAdjust);
            await svc.AdjustAsync(r); return Results.Ok();
        });
    }

    static void Validate(ProductReq r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.Bad("name_required", "El nombre es obligatorio.");
        if (string.IsNullOrWhiteSpace(r.Sku)) throw ApiException.Bad("sku_required", "El código interno (SKU) es obligatorio.");
        if (r.Price < 0 || r.Cost < 0 || r.MinStock < 0) throw ApiException.Bad("invalid_amount", "Precio, costo y stock mínimo no pueden ser negativos.");
    }

    static void Apply(Product p, ProductReq r)
    {
        p.Sku = r.Sku.Trim(); p.Barcode = string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode.Trim();
        p.Name = r.Name.Trim(); p.CategoryId = r.CategoryId; p.UnitId = r.UnitId; p.TaxRateId = r.TaxRateId;
        p.Cost = Money.R(r.Cost); p.Price = Money.R(r.Price); p.MinStock = r.MinStock; p.TracksStock = r.TracksStock; p.Active = r.Active;
    }
}
