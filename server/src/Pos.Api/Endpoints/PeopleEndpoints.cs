using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public record CustomerReq(string Name, string? TaxId, string? Phone, string? Email, decimal? CreditLimit, int? CreditDays, bool Active);
public record SupplierReq(string Name, string? TaxId, string? Phone, int? CreditDays, bool Active);

public static class PeopleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization();

        // ---- Clientes ----
        g.MapGet("/customers", async (AppDb db, string? q, bool? withBalance) =>
        {
            var qry = db.Customers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(c => EF.Functions.ILike(c.Name, $"%{q.Trim()}%") || c.TaxId == q.Trim() || c.Phone == q.Trim());
            if (withBalance == true) qry = qry.Where(c => c.Balance > 0);
            return await qry.OrderBy(c => c.Name).Take(200).Select(c => new { c.Id, c.Name, c.TaxId, c.Phone, c.Email, c.CreditLimit, c.CreditDays, c.Balance, Available = c.CreditLimit - c.Balance, c.Active }).ToListAsync();
        });

        g.MapPost("/customers", async (CustomerReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.CustomersManage);
            if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.Bad("name_required", "El nombre es obligatorio.");
            var c = new Customer { Name = r.Name.Trim(), TaxId = r.TaxId, Phone = r.Phone, Email = r.Email, Active = r.Active };
            if (r.CreditLimit is > 0) { cu.Require(Perm.CreditLimit); c.CreditLimit = Money.R(r.CreditLimit.Value); }
            c.CreditDays = r.CreditDays ?? 30;
            db.Customers.Add(c); audit.Log("customer.create", "customer", c.Id, $"{c.Name} límite={c.CreditLimit}");
            await db.SaveChangesAsync(); return Results.Created($"/api/customers/{c.Id}", new { c.Id });
        });

        g.MapPut("/customers/{id:guid}", async (Guid id, CustomerReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.CustomersManage);
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Cliente no encontrado.");
            c.Name = r.Name.Trim(); c.TaxId = r.TaxId; c.Phone = r.Phone; c.Email = r.Email; c.Active = r.Active; c.CreditDays = r.CreditDays ?? c.CreditDays;
            if (r.CreditLimit is not null && Money.R(r.CreditLimit.Value) != c.CreditLimit)
            {
                cu.Require(Perm.CreditLimit);   // solo supervisor/admin cambia límites
                if (r.CreditLimit < 0) throw ApiException.Bad("invalid_amount", "Límite inválido.");
                audit.Log("customer.credit_limit", "customer", c.Id, $"{c.CreditLimit} → {r.CreditLimit}");
                c.CreditLimit = Money.R(r.CreditLimit.Value);
            }
            await db.SaveChangesAsync(); return Results.Ok();
        });

        g.MapGet("/customers/{id:guid}/statement", async (Guid id, AppDb db) =>
        {
            var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Cliente no encontrado.");
            var now = DateTime.UtcNow;
            var sales = await db.Sales.AsNoTracking().Where(s => s.CustomerId == id).OrderByDescending(s => s.CreatedAt).Take(100)
                .Select(s => new { s.Id, s.Number, s.CreatedAt, Status = s.Status.ToString(), s.Total, s.CreditAmount, s.CreditBalance, s.DueDate, Overdue = s.CreditBalance > 0 && s.DueDate < now }).ToListAsync();
            var pays = await db.CustomerPayments.AsNoTracking().Where(p => p.CustomerId == id).OrderByDescending(p => p.CreatedAt).Take(100)
                .Select(p => new { p.Id, p.Number, p.CreatedAt, Method = p.Method.ToString(), p.Amount, p.Reference }).ToListAsync();
            return new { customer = new { c.Id, c.Name, c.CreditLimit, c.Balance, Available = c.CreditLimit - c.Balance, c.CreditDays }, sales, payments = pays };
        });

        g.MapPost("/customers/payments", async (CustomerPaymentReq r, CreditService svc, CurrentUser cu) => { cu.Require(Perm.CustomersManage); return Results.Ok(await svc.PayAsync(r)); });

        // ---- Proveedores y compras ----
        g.MapGet("/suppliers", async (AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.PurchasesManage);
            return await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.TaxId, s.Phone, s.CreditDays, s.Balance, s.Active }).ToListAsync();
        });
        g.MapPost("/suppliers", async (SupplierReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.PurchasesManage);
            if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.Bad("name_required", "El nombre es obligatorio.");
            var s = new Supplier { Name = r.Name.Trim(), TaxId = r.TaxId, Phone = r.Phone, CreditDays = r.CreditDays ?? 30, Active = r.Active };
            db.Suppliers.Add(s); audit.Log("supplier.create", "supplier", s.Id, s.Name); await db.SaveChangesAsync(); return Results.Created($"/api/suppliers/{s.Id}", new { s.Id });
        });
        g.MapPut("/suppliers/{id:guid}", async (Guid id, SupplierReq r, AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.PurchasesManage);
            var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Proveedor no encontrado.");
            s.Name = r.Name.Trim(); s.TaxId = r.TaxId; s.Phone = r.Phone; s.CreditDays = r.CreditDays ?? s.CreditDays; s.Active = r.Active;
            await db.SaveChangesAsync(); return Results.Ok();
        });

        g.MapPost("/purchases", async (CreatePurchaseReq r, PurchaseService svc, CurrentUser cu) =>
        {
            cu.Require(Perm.PurchasesManage);
            var p = await svc.ReceiveAsync(r);
            return Results.Json(new { p.Id, p.Number, p.Total }, statusCode: 201);
        });
        g.MapGet("/purchases", async (AppDb db, CurrentUser cu, Guid? supplierId, bool? pending) =>
        {
            cu.Require(Perm.PurchasesManage);
            var q = db.Purchases.AsNoTracking().AsQueryable();
            if (supplierId is not null) q = q.Where(p => p.SupplierId == supplierId);
            if (pending == true) q = q.Where(p => p.Total > p.Paid);
            var now = DateTime.UtcNow;
            return await q.OrderByDescending(p => p.ReceivedAt).Take(200).Select(p => new
            { p.Id, p.Number, p.ReceivedAt, Supplier = p.Supplier!.Name, p.SupplierInvoice, p.Total, p.Paid, Balance = p.Total - p.Paid, p.DueDate, Overdue = p.Total > p.Paid && p.DueDate < now }).ToListAsync();
        });
        g.MapPost("/suppliers/payments", async (SupplierPaymentReq r, PurchaseService svc, CurrentUser cu) =>
        {
            cu.Require(Perm.PurchasesManage);
            var (p, bal) = await svc.PayAsync(r);
            return Results.Ok(new { p.Id, p.Amount, balance = bal });
        });
    }
}
