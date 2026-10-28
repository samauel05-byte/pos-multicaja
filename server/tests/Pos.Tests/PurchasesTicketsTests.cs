namespace Pos.Tests;

public class PurchasesTicketsTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Compra_actualiza_existencias_costo_cuenta_por_pagar_y_pagos()
    {
        var sup = await Api.LoginAsync(f, "supervisor"); var admin = await Api.AdminAsync(f);
        var loc = await Seeds.LocationId(admin);
        var supplier = (await sup.GetAsync("/api/suppliers")).Body.EnumerateArray().First().GetProperty("id").GetGuid();
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        var stock0 = await Seeds.Stock(admin, "BEB-001");

        var key = Guid.NewGuid();
        var body = new { operationKey = key, supplierId = supplier, locationId = loc, supplierInvoice = "F-100", lines = new[] { new { productId = pid, quantity = 10m, unitCost = 14m } } };
        var r = await sup.PostAsync("/api/purchases", body);
        Assert.True(r.Ok, r.Body.ToString());
        Assert.Equal(165.20m, r["total"].GetDecimal());                // 140 + 18% = 165.20
        var again = await sup.PostAsync("/api/purchases", body);       // reenvío
        Assert.Equal(r["number"].GetString(), again["number"].GetString());

        Assert.Equal(stock0 + 10, await Seeds.Stock(admin, "BEB-001"));
        Assert.Equal(14m, (await Seeds.Product(admin, "BEB-001")).GetProperty("cost").GetDecimal());
        var sups = (await sup.GetAsync("/api/suppliers")).Body.EnumerateArray().First(s => s.GetProperty("id").GetGuid() == supplier);
        Assert.Equal(165.20m, sups.GetProperty("balance").GetDecimal());

        var pay = await sup.PostAsync("/api/suppliers/payments", new { operationKey = Guid.NewGuid(), supplierId = supplier, method = "Transfer", amount = 100m, reference = "TRX-9" });
        Assert.True(pay.Ok, pay.Body.ToString()); Assert.Equal(65.20m, pay["balance"].GetDecimal());
        var over = await sup.PostAsync("/api/suppliers/payments", new { operationKey = Guid.NewGuid(), supplierId = supplier, method = "Cash", amount = 500m });
        Assert.Equal("overpayment", over.Code);
        var pending = await sup.GetAsync("/api/reports/payables");
        Assert.Contains(pending.Body.EnumerateArray(), x => x.GetProperty("balance").GetDecimal() == 65.20m);
    }

    [Fact]
    public async Task Ajuste_de_inventario_exige_motivo_y_no_deja_stock_negativo()
    {
        var sup = await Api.LoginAsync(f, "supervisor"); var admin = await Api.AdminAsync(f);
        var loc = await Seeds.LocationId(admin); var pid = (await Seeds.Product(admin, "SNK-004")).GetProperty("id").GetGuid();
        var stock0 = await Seeds.Stock(admin, "SNK-004");
        Assert.Equal("reason_required", (await sup.PostAsync("/api/inventory/adjust", new { productId = pid, locationId = loc, quantity = 1m, isOut = true, reason = "" })).Code);
        Assert.Equal("insufficient_stock", (await sup.PostAsync("/api/inventory/adjust", new { productId = pid, locationId = loc, quantity = stock0 + 1, isOut = true, reason = "Merma" })).Code);
        Assert.True((await sup.PostAsync("/api/inventory/adjust", new { productId = pid, locationId = loc, quantity = 2m, isOut = true, reason = "Producto vencido" })).Ok);
        Assert.Equal(stock0 - 2, await Seeds.Stock(admin, "SNK-004"));
        var k = await sup.GetAsync($"/api/inventory/kardex/{pid}");
        Assert.Equal("AdjustmentOut", k[0].GetProperty("type").GetString());
        Assert.Equal(stock0 - 2, k[0].GetProperty("balanceAfter").GetDecimal());
        var low = await sup.GetAsync("/api/reports/low-stock");
        Assert.Contains(low.Body.EnumerateArray(), x => x.GetProperty("sku").GetString() == "SNK-004");   // stock 3 ≤ mínimo 10
    }

    [Fact]
    public async Task Reimprimir_no_duplica_la_venta_y_una_falla_de_impresion_no_la_cancela()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)]);
        var id = sale["sale"].GetProperty("id").GetGuid();
        var salesBefore = await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Sales\"");
        var stockBefore = await Seeds.Stock(admin, "BEB-001");

        var t1 = await c1.PostAsync($"/api/sales/{id}/ticket");
        Assert.False(t1["isCopy"].GetBoolean());
        Assert.DoesNotContain("COPIA", t1["text"].GetString());
        // Simula que la impresora estaba apagada
        var fail = await c1.PostAsync($"/api/print-jobs/{t1["printJobId"].GetGuid()}/result", new { ok = false, error = "Impresora sin papel" });
        Assert.True(fail.Ok);
        var t2 = await c1.PostAsync($"/api/sales/{id}/ticket?reprint=true");
        Assert.True(t2["isCopy"].GetBoolean()); Assert.Contains("COPIA", t2["text"].GetString());
        Assert.Equal(2, t2["printCount"].GetInt32());

        Assert.Equal(salesBefore, await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Sales\""));       // sin ventas nuevas
        Assert.Equal(stockBefore, await Seeds.Stock(admin, "BEB-001"));                                           // sin movimientos
        Assert.Equal("Completed", (await c1.GetAsync($"/api/sales/{id}"))["status"].GetString());                  // venta intacta
        Assert.Equal(1L, await Sql.Scalar<long>(f.ConnString, $"select count(*) from \"PrintJobs\" where \"SaleId\"='{id}' and \"Status\"=3"));
        // ESC/POS: inicializa (ESC @) y corta (GS V) — el contenido es válido, la impresora real queda SIN VALIDAR
        var bytes = Convert.FromBase64String(t1["escPosBase64"].GetString()!);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes[^4..]);
    }
}
