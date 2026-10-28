using System.Text.Json;
namespace Pos.Tests;

public class PermissionsTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Sin_sesion_todo_responde_401_y_salud_es_publica()
    {
        var anon = new Api(f);
        foreach (var url in new[] { "/api/products", "/api/bootstrap", "/api/sales", "/api/reports/sales", "/api/users" })
            Assert.Equal(401, (int)(await anon.GetAsync(url)).Status);
        Assert.Equal(401, (int)(await anon.PostAsync("/api/sales", new { })).Status);
        Assert.True((await anon.GetAsync("/api/health")).Ok);
    }

    [Fact]
    public async Task Cajero_no_puede_administrar_ni_ver_reportes_ni_ajustar_inventario()
    {
        var c = await Api.LoginAsync(f, "cajero1");
        var admin = await Api.AdminAsync(f);
        var p = await Seeds.Product(admin, "BEB-001");
        var body = new { sku = "X-1", barcode = (string?)null, name = "Hack", categoryId = (Guid?)null, unitId = p.GetProperty("unitId").GetGuid(), taxRateId = p.GetProperty("taxRateId").GetGuid(), cost = 0m, price = 1m, minStock = 0m, tracksStock = true, active = true };
        Assert.Equal(403, (int)(await c.PostAsync("/api/products", body)).Status);
        Assert.Equal(403, (int)(await c.PutAsync($"/api/products/{p.GetProperty("id").GetGuid()}", body)).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/reports/sales")).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/reports/low-stock")).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/users")).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/audit")).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/backups")).Status);
        Assert.Equal(403, (int)(await c.PostAsync("/api/backups")).Status);
        Assert.Equal(403, (int)(await c.GetAsync("/api/suppliers")).Status);
        Assert.Equal(403, (int)(await c.PostAsync("/api/inventory/adjust", new { productId = p.GetProperty("id").GetGuid(), locationId = await Seeds.LocationId(admin), quantity = 1m, isOut = false, reason = "x" })).Status);
        Assert.Equal(403, (int)(await c.PutAsync("/api/settings/company", new { name = "x", currencyCode = "DOP", currencySymbol = "RD$", pricesIncludeTax = true, allowNegativeStock = true, cashierMaxDiscountPercent = 100m, supervisorMaxDiscountPercent = 100m, cashDifferenceToleranceAmount = 0m })).Status);
        // ocultar costos al cajero
        var list = await c.GetAsync("/api/products?q=BEB-001");
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("cost").ValueKind);
    }

    [Fact]
    public async Task Supervisor_gestiona_catalogo_pero_no_usuarios_ni_respaldos()
    {
        var s = await Api.LoginAsync(f, "supervisor");
        Assert.True((await s.GetAsync("/api/reports/sales")).Ok);
        Assert.Equal(403, (int)(await s.GetAsync("/api/users")).Status);
        Assert.Equal(403, (int)(await s.PostAsync("/api/backups")).Status);
        var a = await Api.AdminAsync(f);
        Assert.True((await a.GetAsync("/api/users")).Ok);
    }

    [Fact]
    public async Task Un_cajero_no_ve_ni_usa_el_turno_ni_las_ventas_de_otro()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var c2 = await Api.LoginAsync(f, "cajero2");
        var shift = await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        var mine = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)]);
        var saleId = mine["sale"].GetProperty("id").GetGuid();

        Assert.Equal(403, (int)(await Seeds.Sell(c2, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)])).Status);    // turno ajeno
        Assert.Equal(403, (int)(await c2.GetAsync($"/api/sales/{saleId}")).Status);
        Assert.Equal(403, (int)(await c2.PostAsync($"/api/sales/{saleId}/ticket")).Status);
        Assert.Equal(403, (int)(await c2.GetAsync($"/api/shifts/{shift}/summary")).Status);
        Assert.DoesNotContain((await c2.GetAsync("/api/sales")).Body.EnumerateArray(), x => x.GetProperty("id").GetGuid() == saleId);
    }

    [Fact]
    public async Task Login_fallido_se_audita_y_bloquea_tras_cinco_intentos()
    {
        var admin = await Api.AdminAsync(f);
        var mk = await admin.PostAsync("/api/users", new { username = "bloqueable", fullName = "Usuario de prueba", role = "Cashier", password = "Demo1234!", pin = (string?)null, active = true });
        Assert.True(mk.Ok, mk.Body.ToString());
        var anon = new Api(f);
        for (var i = 0; i < 5; i++)
            Assert.Equal(401, (int)(await anon.PostAsync("/api/auth/login", new { username = "bloqueable", password = "mala" })).Status);
        var locked = await anon.PostAsync("/api/auth/login", new { username = "bloqueable", password = "Demo1234!" });   // aun con la clave buena
        Assert.Equal(429, (int)locked.Status); Assert.Equal("locked", locked.Code);
        var audit = await admin.GetAsync("/api/audit?action=login.failed");
        Assert.True(audit.Body.GetArrayLength() >= 5);
        // Los hashes nunca viajan por la API
        var users = await admin.GetAsync("/api/users");
        Assert.DoesNotContain("passwordHash", users.Body.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinHash", users.Body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acciones_sensibles_quedan_en_la_auditoria()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)]);
        var id = sale["sale"].GetProperty("id").GetGuid();
        await c1.PostAsync($"/api/sales/{id}/ticket?reprint=true");
        await c1.PostAsync($"/api/sales/{id}/void", new { operationKey = Guid.NewGuid(), registerId = reg, reason = "prueba", authorizerUsername = "supervisor", authorizerPin = "1234" });
        await admin.PostAsync("/api/inventory/adjust", new { productId = pid, locationId = await Seeds.LocationId(admin), quantity = 1m, isOut = true, reason = "Merma" });
        var actions = (await admin.GetAsync("/api/audit?limit=500")).Body.EnumerateArray().Select(a => a.GetProperty("action").GetString()).ToHashSet();
        foreach (var a in new[] { "shift.open", "sale.create", "ticket.reprint", "sale.void", "inventory.adjust", "authorization.granted" })
            Assert.Contains(a, actions);
    }
}
