using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

/// <summary>Error de negocio con código HTTP y código estable para el cliente.</summary>
public class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static ApiException Bad(string code, string msg) => new(400, code, msg);
    public static ApiException Forbidden(string msg = "No tiene permiso para esta acción.") => new(403, "forbidden", msg);
    public static ApiException NotFound(string msg) => new(404, "not_found", msg);
    public static ApiException Conflict(string code, string msg) => new(409, code, msg);
}

public static class Money
{
    /// <summary>Redondeo definido: 2 decimales, mitad se aleja de cero (AwayFromZero), por línea.</summary>
    public static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal Q(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}

public record LineCalc(decimal Gross, decimal Discount, decimal Net, decimal Tax, decimal Total, decimal EffectivePercent);

public static class Pricing
{
    /// <summary>Cálculo único de línea. Se ejecuta SIEMPRE en el servidor.</summary>
    public static LineCalc Line(decimal qty, decimal unitPrice, decimal? discPercent, decimal? discAmount, decimal taxRate, bool pricesIncludeTax)
    {
        var gross = Money.R(qty * unitPrice);
        decimal disc = 0;
        if (discPercent is > 0) disc = Money.R(gross * discPercent.Value / 100m);
        else if (discAmount is > 0) disc = Money.R(discAmount.Value);
        if (disc > gross) throw ApiException.Bad("discount_too_high", "El descuento no puede superar el importe de la línea.");
        var after = gross - disc;
        decimal net, tax, total;
        if (pricesIncludeTax)
        {
            total = after;
            net = Money.R(total / (1 + taxRate));
            tax = total - net;
        }
        else
        {
            net = after;
            tax = Money.R(net * taxRate);
            total = net + tax;
        }
        var pct = gross == 0 ? 0 : Money.R(disc / gross * 100m);
        return new LineCalc(gross, disc, net, tax, total, pct);
    }
}

public static class Perm
{
    public const string UsersManage = "users.manage";
    public const string SettingsManage = "settings.manage";
    public const string ProductsManage = "products.manage";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryView = "inventory.view";
    public const string ReportsView = "reports.view";
    public const string AuditView = "audit.view";
    public const string PurchasesManage = "purchases.manage";
    public const string CustomersManage = "customers.manage";
    public const string CreditLimit = "customers.credit_limit";
    public const string ReturnsCreate = "returns.create";   // sin autorizador
    public const string SalesVoid = "sales.void";           // sin autorizador
    public const string ShiftsCloseAny = "shifts.close_any";
    public const string BackupManage = "backup.manage";

    static readonly string[] SupervisorSet =
    [
        ProductsManage, InventoryAdjust, InventoryView, ReportsView, AuditView, PurchasesManage, CustomersManage,
        CreditLimit, ReturnsCreate, SalesVoid, ShiftsCloseAny
    ];

    public static IEnumerable<string> For(Role role) => role switch
    {
        Role.Admin => SupervisorSet.Concat([UsersManage, SettingsManage, BackupManage]),
        Role.Supervisor => SupervisorSet,
        _ => [InventoryView, CustomersManage],
    };

    public static bool Has(Role role, string perm) => For(role).Contains(perm);
}

public class CurrentUser(IHttpContextAccessor http)
{
    ClaimsPrincipal P => http.HttpContext?.User ?? new ClaimsPrincipal();
    public bool IsAuthenticated => P.Identity?.IsAuthenticated == true;
    public Guid Id => Guid.TryParse(P.FindFirstValue("uid"), out var g) ? g : throw ApiException.Forbidden("Sesión inválida.");
    public string Username => P.FindFirstValue("name") ?? "";
    public Role Role => Enum.TryParse<Role>(P.FindFirstValue("role"), out var r) ? r : Role.Cashier;
    public string? Ip => http.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool Can(string perm) => Perm.Has(Role, perm);
    public void Require(string perm) { if (!Can(perm)) throw ApiException.Forbidden(); }
}

public static class PasswordHasher
{
    const int Iter = 210_000;
    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iter, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iter}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}";
    }
    public static bool Verify(string secret, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 4 || p[0] != "v1") return false;
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var h = Rfc2898DeriveBytes.Pbkdf2(secret, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(h, expected);
    }
}

public class AuditService(AppDb db, CurrentUser user)
{
    public void Log(string action, string? entity = null, object? id = null, string? detail = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = user.IsAuthenticated ? user.Id : null,
            Username = user.IsAuthenticated ? user.Username : null,
            Action = action, Entity = entity, EntityId = id?.ToString(), Detail = detail, Ip = user.Ip,
        });
    }
    /// <summary>Registra y persiste de inmediato (para eventos que ocurren aunque la operación falle, p. ej. login fallido).</summary>
    public async Task LogNowAsync(string action, string? detail = null, string? username = null)
    {
        db.AuditLogs.Add(new AuditLog { Username = username ?? (user.IsAuthenticated ? user.Username : null), Action = action, Detail = detail, Ip = user.Ip });
        await db.SaveChangesAsync();
    }
}

/// <summary>Autorización por supervisor mediante usuario + PIN, verificada en el servidor.</summary>
public class AuthorizerService(AppDb db, AuditService audit, CurrentUser cu, IServiceScopeFactory scopes)
{
    public async Task<AppUser> VerifyAsync(string? username, string? pin, string action)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
            throw new ApiException(403, "authorization_required", "Se requiere autorización de un supervisor.");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Username == username.Trim().ToLower() && x.Active);
        if (u is null || u.PinHash is null || u.Role == Role.Cashier || u.LockedUntil > DateTime.UtcNow || !PasswordHasher.Verify(pin, u.PinHash))
        {
            // El fallo se registra en una conexión independiente: la venta/devolución en curso se revierte, pero el intento queda auditado.
            await RecordFailureAsync(u?.Id, u is not null && u.Role != Role.Cashier, $"{action} / solicitado por {cu.Username} / autorizador {username}");
            throw new ApiException(403, "authorization_invalid", "Autorización inválida.");
        }
        u.FailedLogins = 0;
        audit.Log("authorization.granted", "user", u.Id, $"{action} / solicitado por {cu.Username}");
        return u;
    }

    async Task RecordFailureAsync(Guid? targetUserId, bool countAttempt, string detail)
    {
        using var scope = scopes.CreateScope();
        var other = scope.ServiceProvider.GetRequiredService<AppDb>();
        other.AuditLogs.Add(new AuditLog { UserId = cu.IsAuthenticated ? cu.Id : null, Username = cu.IsAuthenticated ? cu.Username : null, Action = "authorization.failed", Entity = "user", EntityId = targetUserId?.ToString(), Detail = detail, Ip = cu.Ip });
        if (countAttempt && targetUserId is not null)
        {
            var t = await other.Users.FirstAsync(x => x.Id == targetUserId);
            t.FailedLogins++;
            if (t.FailedLogins >= 5) { t.LockedUntil = DateTime.UtcNow.AddMinutes(5); t.FailedLogins = 0; }
        }
        await other.SaveChangesAsync();
    }
}

public class Numbering(AppDb db)
{
    /// <summary>Consecutivo atómico (UPSERT ... RETURNING) dentro de la transacción actual.</summary>
    public async Task<string> NextAsync(string key, string prefix, int width = 6)
    {
        var n = await db.ScalarAsync<long>(
            "INSERT INTO \"DocumentCounters\" (\"Key\",\"Value\") VALUES (@k,1) ON CONFLICT (\"Key\") DO UPDATE SET \"Value\" = \"DocumentCounters\".\"Value\" + 1 RETURNING \"Value\"",
            new NpgsqlParameter("k", key));
        return $"{prefix}-{n.ToString().PadLeft(width, '0')}";
    }
}

public static class DbExt
{
    public static async Task<T> ScalarAsync<T>(this AppDb db, string sql, params NpgsqlParameter[] ps)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var p in ps) cmd.Parameters.Add(p);
        var o = await cmd.ExecuteScalarAsync();
        if (o is null || o is DBNull) return default!;
        return (T)Convert.ChangeType(o, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static bool IsUniqueViolation(this DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
