using Xunit.Abstractions;
namespace Pos.Tests;

public class ConcurrencyAndIdempotencyTests(PosFixture f, ITestOutputHelper log) : IClassFixture<PosFixture>
{
    static async Task<(Guid unit, Guid tax, Guid loc)> Refs(Api admin)
    {
        var b = await admin.GetAsync("/api/bootstrap");
        return (b["units"][0].GetProperty("id").GetGuid(), b["taxRates"][0].GetProperty("id").GetGuid(), b["locations"][0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Dos_cajas_vendiendo_la_ultima_unidad_solo_una_gana()
    {
        var admin = await Api.AdminAsync(f);
        var c1 = await Api.LoginAsync(f, "cajero1"); var c2 = await Api.LoginAsync(f, "cajero2");
        await Seeds.FreshShift(f, c1, "Caja 1"); await Seeds.FreshShift(f, c2, "Caja 2");
        var r1 = await Seeds.RegisterId(admin, "Caja 1"); var r2 = await Seeds.RegisterId(admin, "Caja 2");
        var (unit, tax, loc) = await Refs(admin);

        int wins1 = 0, wins2 = 0;
        for (var round = 0; round < 25; round++)
        {
            var created = await admin.PostAsync("/api/products", new { sku = $"LAST-{round}", barcode = (string?)null, name = $"Última {round}", categoryId = (Guid?)null, unitId = unit, taxRateId = tax,
                cost = 10m, price = 100m, minStock = 0m, tracksStock = true, active = true, initialStock = 1m, initialLocationId = loc });
            Assert.True(created.Ok, created.Body.ToString());
            var pid = created["id"].GetGuid();

            var barrier = new TaskCompletionSource();
            async Task<Res> Go(Api a, Guid reg) { await barrier.Task; return await Seeds.Sell(a, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(100)]); }
            var t1 = Task.Run(() => Go(c1, r1)); var t2 = Task.Run(() => Go(c2, r2));
            barrier.SetResult();
            var res = await Task.WhenAll(t1, t2);

            Assert.Single(res, r => r.Ok);                                   // exactamente una venta
            var loser = res.Single(r => !r.Ok);
            Assert.Equal(409, (int)loser.Status);
            Assert.Equal("insufficient_stock", loser.Code);
            if (res[0].Ok) wins1++; else wins2++;

            var stock = await Sql.Scalar<decimal>(f.ConnString, $"select \"Quantity\" from \"StockLevels\" where \"ProductId\"='{pid}'");
            Assert.Equal(0m, stock);                                        // nunca negativo
            var sales = await Sql.Scalar<long>(f.ConnString, $"select count(*) from \"SaleLines\" where \"ProductId\"='{pid}'");
            Assert.Equal(1, sales);
            var kardexSales = await Sql.Scalar<long>(f.ConnString, $"select count(*) from \"InventoryMovements\" where \"ProductId\"='{pid}' and \"Type\"=2");
            Assert.Equal(1, kardexSales);
        }
        log.WriteLine($"25 rondas: caja1 ganó {wins1}, caja2 ganó {wins2}; nunca hubo sobreventa.");
    }

    [Fact]
    public async Task Reenvio_del_mismo_cobro_no_duplica_venta_ni_inventario_ni_efectivo()
    {
        var c1 = await Api.LoginAsync(f, "cajero1");
        var shift = await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f);
        var reg = await Seeds.RegisterId(admin, "Caja 1");
        var agua = await Seeds.Product(admin, "BEB-001");
        var pid = agua.GetProperty("id").GetGuid();
        var before = await Seeds.Stock(admin, "BEB-001");

        var key = Guid.NewGuid();
        var first = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(100)], key);
        Assert.Equal(201, (int)first.Status); Assert.False(first["duplicate"].GetBoolean());
        var second = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(100)], key);
        Assert.Equal(200, (int)second.Status); Assert.True(second["duplicate"].GetBoolean());
        Assert.Equal(first["sale"].GetProperty("number").GetString(), second["sale"].GetProperty("number").GetString());

        // Ráfaga concurrente con la misma clave (p. ej. doble clic + reintento de red)
        var key2 = Guid.NewGuid();
        var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)], key2))));
        Assert.All(burst, r => Assert.True(r.Ok, r.Body.ToString()));
        Assert.Single(burst.Select(r => r["sale"].GetProperty("number").GetString()).Distinct());
        Assert.Equal(1, burst.Count(r => !r["duplicate"].GetBoolean()));

        Assert.Equal(before - 3, await Seeds.Stock(admin, "BEB-001"));                  // 2 + 1, no 2*2 + 8
        var cashMoves = await Sql.Scalar<long>(f.ConnString, $"select count(*) from \"CashMovements\" where \"ShiftId\"='{shift}' and \"Type\"=4");
        Assert.Equal(2, cashMoves);

        // Consulta de estado tras un corte de red: existe / no existe
        var q1 = await c1.GetAsync($"/api/sales/by-operation/{key}");
        Assert.True(q1["exists"].GetBoolean());
        var q2 = await c1.GetAsync($"/api/sales/by-operation/{Guid.NewGuid()}");
        Assert.False(q2["exists"].GetBoolean());
    }

    [Fact]
    public async Task Apertura_simultanea_de_la_misma_caja_solo_deja_un_turno()
    {
        var admin = await Api.AdminAsync(f);
        var c1 = await Api.LoginAsync(f, "cajero1");
        var sup = await Api.LoginAsync(f, "supervisor");
        var reg = await Seeds.RegisterId(admin, "Caja 2");
        await Seeds.FreshShift(f, c1, "Caja 2");  // abre y luego lo cerramos
        var boot = await admin.GetAsync("/api/bootstrap");
        var open = boot["openShifts"].EnumerateArray().First(s => s.GetProperty("registerId").GetGuid() == reg).GetProperty("id").GetGuid();
        var sum = await admin.GetAsync($"/api/shifts/{open}/summary");
        Assert.True((await admin.PostAsync("/api/shifts/close", new { shiftId = open, countedCash = sum["expectedCash"].GetDecimal() })).Ok);

        var results = await Task.WhenAll(
            Task.Run(() => c1.PostAsync("/api/shifts/open", new { registerId = reg, openingFloat = 100m })),
            Task.Run(() => sup.PostAsync("/api/shifts/open", new { registerId = reg, openingFloat = 100m })),
            Task.Run(() => admin.PostAsync("/api/shifts/open", new { registerId = reg, openingFloat = 100m })));
        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal("shift_already_open", r.Code));
    }
}
