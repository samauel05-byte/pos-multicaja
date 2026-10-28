using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Pos.Tests;

public record Res(HttpStatusCode Status, JsonElement Body)
{
    public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var c) ? c.GetString() : null;
    public JsonElement this[string name] => Body.GetProperty(name);
    public JsonElement this[int i] => Body[i];
    public bool Ok => (int)Status is >= 200 and < 300;
}

/// <summary>Cada clase de pruebas obtiene su propia base PostgreSQL real (no SQLite ni memoria) con migraciones y datos demo.</summary>
public class PosFixture : IAsyncLifetime
{
    public static string AdminConn => Environment.GetEnvironmentVariable("POS_TEST_ADMIN_CONN") ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
    public string DbName { get; } = "pos_test_" + Guid.NewGuid().ToString("N")[..10];
    public string BackupDir { get; } = Path.Combine(Path.GetTempPath(), "pos-test-backups-" + Guid.NewGuid().ToString("N")[..8]);
    public string ConnString => new NpgsqlConnectionStringBuilder(AdminConn) { Database = DbName }.ConnectionString;
    public WebApplicationFactory<Program> Factory = null!;
    public const string BackupPass = "frase-de-prueba-123";

    protected virtual void Extra(IWebHostBuilder b) { }

    public async Task InitializeAsync()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Db", ConnString);
            b.UseSetting("Jwt:Key", "test-key-test-key-test-key-test-key-123456");
            b.UseSetting("Seed:Demo", "true");
            b.UseSetting("Backup:Passphrase", BackupPass);
            b.UseSetting("Backup:Directory", BackupDir);
            b.UseSetting("Backup:AutoEnabled", "false");
            Extra(b);
            b.UseSetting("Backup:PgBinDir", Environment.GetEnvironmentVariable("POS_PG_BIN") ?? "/usr/lib/postgresql/16/bin");
        });
        _ = Factory.Server;   // arranca: migra y siembra
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(AdminConn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DbName}\" WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
        if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir, true);
    }
}

public class Api
{
    public readonly HttpClient Http;
    public string? Token;
    public Api(PosFixture f) { Http = f.Factory.CreateClient(); }

    public static async Task<Api> LoginAsync(PosFixture f, string user, string pass = "Demo1234!")
    {
        var a = new Api(f);
        var r = await a.PostAsync("/api/auth/login", new { username = user, password = pass });
        Assert.True(r.Ok, $"login {user}: {r.Body}");
        a.Token = r["token"].GetString();
        a.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        return a;
    }
    public static Task<Api> AdminAsync(PosFixture f) => LoginAsync(f, "admin", "Admin1234!");

    static async Task<Res> Wrap(HttpResponseMessage m)
    {
        var s = await m.Content.ReadAsStringAsync();
        JsonElement el = default;
        if (!string.IsNullOrWhiteSpace(s)) { try { el = JsonDocument.Parse(s).RootElement.Clone(); } catch { } }
        return new Res(m.StatusCode, el);
    }
    public async Task<Res> GetAsync(string url) => await Wrap(await Http.GetAsync(url));
    public async Task<Res> PostAsync(string url, object? body = null) => await Wrap(await Http.PostAsJsonAsync(url, body ?? new { }));
    public async Task<Res> PutAsync(string url, object body) => await Wrap(await Http.PutAsJsonAsync(url, body));
}

public static class Seeds
{
    // Referencias a los datos de demostración
    public static async Task<Guid> RegisterId(Api a, string name)
    {
        var b = await a.GetAsync("/api/bootstrap");
        return b["registers"].EnumerateArray().First(r => r.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }
    public static async Task<Guid> LocationId(Api a) => (await a.GetAsync("/api/bootstrap"))["locations"][0].GetProperty("id").GetGuid();

    public static async Task<JsonElement> Product(Api a, string sku)
    {
        var r = await a.GetAsync($"/api/products?q={sku}");
        return r.Body.EnumerateArray().First(p => p.GetProperty("sku").GetString() == sku);
    }
    public static async Task<decimal> Stock(Api a, string sku) => (await Product(a, sku)).GetProperty("stock").GetDecimal();

    /// <summary>Cierra cualquier turno abierto de la caja (como admin) y abre uno nuevo con el usuario dado.</summary>
    public static async Task<Guid> FreshShift(PosFixture f, Api cashier, string register, decimal floatAmt = 1000m)
    {
        var admin = await Api.AdminAsync(f);
        var reg = await RegisterId(admin, register);
        var boot = await admin.GetAsync("/api/bootstrap");
        foreach (var s in boot["openShifts"].EnumerateArray().Where(s => s.GetProperty("registerId").GetGuid() == reg))
        {
            var sum = await admin.GetAsync($"/api/shifts/{s.GetProperty("id").GetGuid()}/summary");
            var c = await admin.PostAsync("/api/shifts/close", new { shiftId = s.GetProperty("id").GetGuid(), countedCash = sum["expectedCash"].GetDecimal(), notes = "limpieza de prueba" });
            Assert.True(c.Ok, c.Body.ToString());
        }
        var o = await cashier.PostAsync("/api/shifts/open", new { registerId = reg, openingFloat = floatAmt });
        Assert.True(o.Ok, o.Body.ToString());
        return o["shiftId"].GetGuid();
    }

    public static object Line(Guid productId, decimal qty, decimal? discPct = null) => new { productId, quantity = qty, discountPercent = discPct };

    public static async Task<Res> Sell(Api a, Guid register, IEnumerable<object> lines, IEnumerable<object> payments, Guid? key = null, Guid? customer = null, string? authUser = null, string? authPin = null, Guid? quote = null)
        => await a.PostAsync("/api/sales", new { operationKey = key ?? Guid.NewGuid(), registerId = register, customerId = customer, lines, payments, quoteId = quote, authorizerUsername = authUser, authorizerPin = authPin });

    public static object Cash(decimal tendered) => new { method = "Cash", amount = 0m, tendered };
    public static object Pay(string method, decimal amount, string? reference = null) => new { method, amount, reference };
}

public static class Sql
{
    public static async Task<T> Scalar<T>(string connString, string sql)
    {
        await using var c = new NpgsqlConnection(connString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }
}
