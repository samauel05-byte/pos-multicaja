using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public record UserReq(string Username, string FullName, Role Role, string? Password, string? Pin, bool Active);
public record CompanyReq(string Name, string? TaxId, string CurrencyCode, string CurrencySymbol, bool PricesIncludeTax, bool AllowNegativeStock,
    decimal CashierMaxDiscountPercent, decimal SupervisorMaxDiscountPercent, decimal CashDifferenceToleranceAmount, string? TicketFooter);

public static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization();

        g.MapGet("/users", async (AppDb db, CurrentUser cu) =>
        {
            cu.Require(Perm.UsersManage);
            return await db.Users.AsNoTracking().OrderBy(u => u.Username).Select(u => new { u.Id, u.Username, u.FullName, Role = u.Role.ToString(), u.Active, HasPin = u.PinHash != null }).ToListAsync();
        });

        g.MapPost("/users", async (UserReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.UsersManage);
            var username = r.Username.Trim().ToLower();
            if (username.Length < 3) throw ApiException.Bad("invalid_username", "Usuario demasiado corto.");
            ValidateSecrets(r);
            if (string.IsNullOrEmpty(r.Password)) throw ApiException.Bad("password_required", "La contraseña es obligatoria.");
            var u = new AppUser { Username = username, FullName = r.FullName.Trim(), Role = r.Role, PasswordHash = PasswordHasher.Hash(r.Password), Active = r.Active,
                PinHash = string.IsNullOrEmpty(r.Pin) ? null : PasswordHasher.Hash(r.Pin) };
            db.Users.Add(u);
            audit.Log("user.create", "user", u.Id, $"{u.Username} rol={u.Role}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{u.Id}", new { u.Id });
        });

        g.MapPut("/users/{id:guid}", async (Guid id, UserReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.UsersManage);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Usuario no encontrado.");
            ValidateSecrets(r);
            if (u.Id == cu.Id && (!r.Active || r.Role != Role.Admin)) throw ApiException.Bad("self_lockout", "No puede desactivarse ni quitarse el rol de administrador a sí mismo.");
            var roleChanged = u.Role != r.Role;
            u.FullName = r.FullName.Trim(); u.Role = r.Role; u.Active = r.Active;
            if (!string.IsNullOrEmpty(r.Password)) u.PasswordHash = PasswordHasher.Hash(r.Password);
            if (!string.IsNullOrEmpty(r.Pin)) u.PinHash = PasswordHasher.Hash(r.Pin);
            audit.Log("user.update", "user", u.Id, $"{u.Username} rol={u.Role} activo={u.Active}{(roleChanged ? " (cambio de rol)" : "")}{(string.IsNullOrEmpty(r.Password) ? "" : " (contraseña cambiada)")}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/audit", async (AppDb db, CurrentUser cu, string? action, DateTime? from, DateTime? to, int? limit) =>
        {
            cu.Require(Perm.AuditView);
            var q = db.AuditLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
            if (from is not null) q = q.Where(a => a.At >= from.Value.ToUniversalTime());
            if (to is not null) q = q.Where(a => a.At < to.Value.ToUniversalTime().AddDays(1));
            return await q.OrderByDescending(a => a.Id).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync();
        });

        g.MapPut("/settings/company", async (CompanyReq r, AppDb db, CurrentUser cu, AuditService audit) =>
        {
            cu.Require(Perm.SettingsManage);
            if (r.CashierMaxDiscountPercent is < 0 or > 100 || r.SupervisorMaxDiscountPercent is < 0 or > 100) throw ApiException.Bad("invalid_percent", "Porcentaje inválido.");
            var c = await db.Companies.FirstAsync();
            c.Name = r.Name.Trim(); c.TaxId = r.TaxId; c.CurrencyCode = r.CurrencyCode.Trim().ToUpper(); c.CurrencySymbol = r.CurrencySymbol.Trim();
            c.PricesIncludeTax = r.PricesIncludeTax; c.AllowNegativeStock = r.AllowNegativeStock;
            c.CashierMaxDiscountPercent = r.CashierMaxDiscountPercent; c.SupervisorMaxDiscountPercent = r.SupervisorMaxDiscountPercent;
            c.CashDifferenceToleranceAmount = r.CashDifferenceToleranceAmount; c.TicketFooter = r.TicketFooter;
            audit.Log("settings.company", "company", c.Id, "configuración modificada");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/integrations/outbox", async (AppDb db, CurrentUser cu, bool? pending, int? limit) =>
        {
            cu.Require(Perm.SettingsManage);
            var q = db.Outbox.AsNoTracking().AsQueryable();
            if (pending == true) q = q.Where(e => e.ProcessedAt == null);
            return new { pendingCount = await db.Outbox.CountAsync(e => e.ProcessedAt == null), items = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync() };
        });

        // ---- Respaldos ----
        g.MapGet("/backups", async (AppDb db, CurrentUser cu, BackupService svc) =>
        {
            cu.Require(Perm.BackupManage);
            var list = await db.Backups.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync();
            var o = svc.Options;
            return new { autoEnabled = o.AutoEnabled, intervalHours = o.IntervalHours, keepLast = o.KeepLast, directory = o.Directory, passphraseConfigured = !string.IsNullOrWhiteSpace(o.Passphrase), items = list };
        });
        g.MapPost("/backups", async (BackupService svc, CurrentUser cu, AuditService audit, AppDb db) =>
        {
            cu.Require(Perm.BackupManage);
            var r = await svc.CreateAsync("manual");
            audit.Log("backup.create", "backup", r.Id, r.Success ? r.FileName : "FALLÓ: " + r.Error);
            await db.SaveChangesAsync();
            return r.Success ? Results.Ok(r) : Results.Json(new { code = "backup_failed", message = r.Error }, statusCode: 500);
        });
    }

    static void ValidateSecrets(UserReq r)
    {
        if (string.IsNullOrWhiteSpace(r.FullName)) throw ApiException.Bad("name_required", "El nombre es obligatorio.");
        if (!string.IsNullOrEmpty(r.Password) && r.Password.Length < 8) throw ApiException.Bad("weak_password", "La contraseña debe tener al menos 8 caracteres.");
        if (!string.IsNullOrEmpty(r.Pin) && (r.Pin.Length < 4 || !r.Pin.All(char.IsDigit))) throw ApiException.Bad("weak_pin", "El PIN debe tener al menos 4 dígitos.");
    }
}
