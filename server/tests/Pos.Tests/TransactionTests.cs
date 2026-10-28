using Npgsql;
namespace Pos.Tests;

public class AutoBackupFixture : PosFixture
{
    protected override void Extra(Microsoft.AspNetCore.Hosting.IWebHostBuilder b)
    {
        b.UseSetting("Backup:AutoEnabled", "true"); b.UseSetting("Backup:FirstRunDelaySeconds", "1"); b.UseSetting("Backup:IntervalHours", "24");
    }
}

public class TransactionTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Si_una_linea_falla_no_queda_nada_a_medias()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var agua = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        var ultimo = (await Seeds.Product(admin, "SNK-005")).GetProperty("id").GetGuid();     // stock 1
        var stockAgua = await Seeds.Stock(admin, "BEB-001");

        async Task<(long sales, long cash, long kardex, long outbox)> Counts() => (
            await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Sales\""), await Sql.Scalar<long>(f.ConnString, $"select count(*) from \"CashMovements\" where \"ShiftId\"='{shift}'"),
            await Sql.Scalar<long>(f.ConnString, "select count(*) from \"InventoryMovements\""), await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Outbox\""));
        var before = await Counts();

        // Agua alcanza, pero se piden 2 de un producto con 1 en existencia → toda la venta debe revertirse
        var r = await Seeds.Sell(c1, reg, [Seeds.Line(agua, 3), Seeds.Line(ultimo, 2)], [Seeds.Cash(1000)]);
        Assert.Equal("insufficient_stock", r.Code);
        Assert.Equal(before, await Counts());
        Assert.Equal(stockAgua, await Seeds.Stock(admin, "BEB-001"));          // el descuento del agua se revirtió
        Assert.Equal(1m, await Seeds.Stock(admin, "SNK-005"));

        // El consecutivo no queda "quemado" ni hay huecos: la siguiente venta toma el siguiente número
        var n1 = (await Seeds.Sell(c1, reg, [Seeds.Line(agua, 1)], [Seeds.Cash(50)]))["sale"].GetProperty("number").GetString()!;
        var n2 = (await Seeds.Sell(c1, reg, [Seeds.Line(agua, 1)], [Seeds.Cash(50)]))["sale"].GetProperty("number").GetString()!;
        Assert.Equal(int.Parse(n1[2..]) + 1, int.Parse(n2[2..]));

        // Una venta confirmada deja, en la misma transacción: venta, líneas, pago, mov. de caja, kardex y evento de integración
        var after = await Counts();
        Assert.Equal(before.sales + 2, after.sales); Assert.Equal(before.cash + 2, after.cash); Assert.Equal(before.kardex + 2, after.kardex); Assert.Equal(before.outbox + 2, after.outbox);
        var ob = await admin.GetAsync("/api/integrations/outbox?pending=true");
        Assert.True(ob["pendingCount"].GetInt32() >= 2);
        Assert.Contains(ob["items"].EnumerateArray(), e => e.GetProperty("type").GetString() == "sale.completed" && e.GetProperty("payloadJson").GetString()!.Contains(n1));
    }

    [Fact]
    public async Task Cerrar_el_turno_mientras_se_vende_nunca_deja_ventas_fuera_del_cuadre()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var admin = await Api.AdminAsync(f);
        var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        for (var round = 0; round < 12; round++)
        {
            var shift = await Seeds.FreshShift(f, c1, "Caja 1", 100m);
            var sells = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(25)])));
            var close = Task.Run(async () => { await Task.Delay(Random.Shared.Next(0, 60)); return await admin.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = 0m, notes = "carrera" }); });
            var results = await Task.WhenAll(sells);
            var closeRes = await close; Assert.True(closeRes.Ok, closeRes.Body.ToString());
            Assert.All(results.Where(r => !r.Ok), r => Assert.Equal("no_open_shift", r.Code));   // o se cobró antes del cierre, o se rechazó
            var expectedStored = await Sql.Scalar<decimal>(f.ConnString, $"select \"ExpectedCash\" from \"Shifts\" where \"Id\"='{shift}'");
            var movesSum = await Sql.Scalar<decimal>(f.ConnString, $"select coalesce(sum(\"Amount\"),0) from \"CashMovements\" where \"ShiftId\"='{shift}'");
            Assert.Equal(expectedStored, movesSum);                                                 // nada entró a la gaveta después del arqueo
            Assert.Equal(100m + 25m * results.Count(r => r.Ok), movesSum);
        }
    }

    [Fact]
    public async Task Venta_de_varias_lineas_del_mismo_producto_valida_la_suma()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 2");
        var c2 = await Api.LoginAsync(f, "cajero2"); await Seeds.FreshShift(f, c2, "Caja 2");
        var ultimo = (await Seeds.Product(admin, "SNK-005")).GetProperty("id").GetGuid();     // stock 1 (si otro test no lo consumió)
        var stock = await Seeds.Stock(admin, "SNK-005");
        var r = await Seeds.Sell(c2, reg, [Seeds.Line(ultimo, stock), Seeds.Line(ultimo, 1)], [Seeds.Cash(10000)]);
        Assert.Equal("insufficient_stock", r.Code);
        Assert.Equal(stock, await Seeds.Stock(admin, "SNK-005"));
    }
}

public class AutoBackupTests(AutoBackupFixture f) : IClassFixture<AutoBackupFixture>
{
    [Fact]
    public async Task El_respaldo_automatico_se_ejecuta_solo_y_queda_registrado()
    {
        var admin = await Api.AdminAsync(f);
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var n = await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Backups\" where \"Kind\"='auto' and \"Success\"");
            if (n > 0) break;
        }
        var list = await admin.GetAsync("/api/backups");
        var auto = list["items"].EnumerateArray().FirstOrDefault(b => b.GetProperty("kind").GetString() == "auto");
        Assert.True(auto.ValueKind == System.Text.Json.JsonValueKind.Object, "No se creó el respaldo automático");
        Assert.True(auto.GetProperty("success").GetBoolean(), auto.ToString());
        Assert.True(File.Exists(Path.Combine(f.BackupDir, auto.GetProperty("fileName").GetString()!)));
    }
}
