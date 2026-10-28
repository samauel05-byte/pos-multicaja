namespace Pos.Tests;

public class ReturnsAndShiftTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Devolucion_parcial_reingresa_inventario_y_descuenta_efectivo_de_caja()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var sup = await Api.LoginAsync(f, "supervisor");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();     // 25
        var stock0 = await Seeds.Stock(admin, "BEB-001");

        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 4)], [Seeds.Cash(100)]);
        var saleId = sale["sale"].GetProperty("id").GetGuid(); var lineId = sale["sale"].GetProperty("lines")[0].GetProperty("id").GetGuid();
        Assert.Equal(stock0 - 4, await Seeds.Stock(admin, "BEB-001"));

        // El cajero no puede devolver por sí solo
        var denied = await c1.PostAsync("/api/returns", new { operationKey = Guid.NewGuid(), saleId, registerId = reg, reason = "Cliente se arrepintió", restock = true, lines = new[] { new { saleLineId = lineId, quantity = 1m } } });
        Assert.Equal(403, (int)denied.Status); Assert.Equal("authorization_required", denied.Code);

        // Motivo obligatorio
        var noReason = await sup.PostAsync("/api/returns", new { operationKey = Guid.NewGuid(), saleId, registerId = reg, reason = " ", restock = true, lines = new[] { new { saleLineId = lineId, quantity = 1m } } });
        Assert.Equal("reason_required", noReason.Code);

        var key = Guid.NewGuid();
        var body = new { operationKey = key, saleId, registerId = reg, reason = "Producto dañado", restock = true, refundMethod = "Cash", lines = new[] { new { saleLineId = lineId, quantity = 2m } } };
        var ok = await sup.PostAsync("/api/returns", body);
        Assert.True(ok.Ok, ok.Body.ToString());
        Assert.Equal(50m, ok["total"].GetDecimal());
        var dup = await sup.PostAsync("/api/returns", body);                                   // reenvío: no duplica
        Assert.True(dup.Ok); Assert.Equal(ok["number"].GetString(), dup["number"].GetString());

        Assert.Equal(stock0 - 2, await Seeds.Stock(admin, "BEB-001"));
        var sum = await admin.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(1000m + 100m - 50m, sum["expectedCash"].GetDecimal());
        Assert.Equal(50m, sum["cashRefunds"].GetDecimal());

        var over = await sup.PostAsync("/api/returns", new { operationKey = Guid.NewGuid(), saleId, registerId = reg, reason = "x", restock = true, refundMethod = "Cash", lines = new[] { new { saleLineId = lineId, quantity = 3m } } });
        Assert.Equal(409, (int)over.Status); Assert.Equal("over_return", over.Code);

        // La venta original sigue ahí, con su historial, y el kardex muestra el movimiento compensatorio
        var original = await admin.GetAsync($"/api/sales/{saleId}");
        Assert.Equal("Completed", original["status"].GetString()); Assert.Equal(2m, original["lines"][0].GetProperty("returnedQuantity").GetDecimal());
        var kardex = await admin.GetAsync($"/api/inventory/kardex/{pid}");
        Assert.Contains(kardex.Body.EnumerateArray(), m => m.GetProperty("type").GetString() == "Return" && m.GetProperty("quantity").GetDecimal() == 2m);
    }

    [Fact]
    public async Task Devolucion_sin_reingreso_no_mueve_inventario()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var sup = await Api.LoginAsync(f, "supervisor"); var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-002")).GetProperty("id").GetGuid();
        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(100)]);
        var stock1 = await Seeds.Stock(admin, "BEB-002");
        var r = await c1.PostAsync("/api/returns", new { operationKey = Guid.NewGuid(), saleId = sale["sale"].GetProperty("id").GetGuid(), registerId = reg, reason = "Vencido", restock = false, refundMethod = "Cash",
            lines = new[] { new { saleLineId = sale["sale"].GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 1m } }, authorizerUsername = "supervisor", authorizerPin = "1234" });
        Assert.True(r.Ok, r.Body.ToString());
        Assert.Equal(stock1, await Seeds.Stock(admin, "BEB-002"));
    }

    [Fact]
    public async Task Anulacion_conserva_la_venta_revierte_inventario_y_efectivo_y_no_se_repite()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "DES-005")).GetProperty("id").GetGuid();      // 58
        var stock0 = await Seeds.Stock(admin, "DES-005");
        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Pay("Card", 16, "1111"), Seeds.Cash(100)]);    // 116 = 16 tarjeta + 100 efectivo
        var saleId = sale["sale"].GetProperty("id").GetGuid();
        var number = sale["sale"].GetProperty("number").GetString();

        var noAuth = await c1.PostAsync($"/api/sales/{saleId}/void", new { operationKey = Guid.NewGuid(), registerId = reg, reason = "Error de captura" });
        Assert.Equal("authorization_required", noAuth.Code);
        var v = await c1.PostAsync($"/api/sales/{saleId}/void", new { operationKey = Guid.NewGuid(), registerId = reg, reason = "Error de captura", authorizerUsername = "supervisor", authorizerPin = "1234" });
        Assert.True(v.Ok, v.Body.ToString());
        Assert.Equal(116m, v["total"].GetDecimal());

        var got = await admin.GetAsync($"/api/sales/{saleId}");           // NO se borra
        Assert.Equal("Voided", got["status"].GetString()); Assert.Equal(number, got["number"].GetString());
        Assert.Equal(stock0, await Seeds.Stock(admin, "DES-005"));
        var sum = await admin.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(1000m, sum["expectedCash"].GetDecimal());                // efectivo revertido
        Assert.Equal(0m, sum["cardSales"].GetDecimal());                       // tarjeta neta cero (reembolso registrado)
        Assert.Equal(1, sum["voidedCount"].GetInt32());

        var twice = await c1.PostAsync($"/api/sales/{saleId}/void", new { operationKey = Guid.NewGuid(), registerId = reg, reason = "otra vez", authorizerUsername = "supervisor", authorizerPin = "1234" });
        Assert.Equal("sale_voided", twice.Code);
        var after = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(100)]);   // y el sistema sigue vendiendo
        Assert.True(after.Ok);
    }

    [Fact]
    public async Task Devolver_una_venta_a_credito_reduce_primero_el_saldo_del_cliente()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 500m);
        var sup = await Api.LoginAsync(f, "supervisor"); var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "LIM-002")).GetProperty("id").GetGuid();      // 45
        var colmado = (await c1.GetAsync("/api/customers")).Body.EnumerateArray().First(c => c.GetProperty("name").GetString()!.StartsWith("Colmado")).GetProperty("id").GetGuid();
        var before = (await c1.GetAsync($"/api/customers/{colmado}/statement"))["customer"].GetProperty("balance").GetDecimal();

        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 4)], [Seeds.Pay("Credit", 180)], customer: colmado);    // 4×45 = 180 todo a crédito
        Assert.True(sale.Ok, sale.Body.ToString());
        var saleId = sale["sale"].GetProperty("id").GetGuid(); var lineId = sale["sale"].GetProperty("lines")[0].GetProperty("id").GetGuid();
        Assert.Equal(before + 180, (await c1.GetAsync($"/api/customers/{colmado}/statement"))["customer"].GetProperty("balance").GetDecimal());

        var r = await sup.PostAsync("/api/returns", new { operationKey = Guid.NewGuid(), saleId, registerId = reg, reason = "Devolución parcial", restock = true, refundMethod = "Cash",
            lines = new[] { new { saleLineId = lineId, quantity = 1m } } });
        Assert.True(r.Ok, r.Body.ToString());
        Assert.Equal(before + 135, (await c1.GetAsync($"/api/customers/{colmado}/statement"))["customer"].GetProperty("balance").GetDecimal());
        var sum = await admin.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(500m, sum["expectedCash"].GetDecimal());                 // no sale efectivo: se redujo la deuda
        Assert.Equal(135m, sum["creditSales"].GetDecimal());
    }

    [Fact]
    public async Task Cierre_de_caja_esperado_contado_diferencia_y_separacion_por_medio()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        Assert.True((await Seeds.Sell(c1, reg, [Seeds.Line(pid, 3)], [Seeds.Cash(100)])).Ok);                 // +75 efectivo
        Assert.True((await Seeds.Sell(c1, reg, [Seeds.Line(pid, 4)], [Seeds.Pay("Card", 100)])).Ok);           // tarjeta 100 (no entra a la gaveta)
        Assert.True((await c1.PostAsync("/api/shifts/movement", new { shiftId = shift, amount = 200m, reason = "Cambio del banco", isOut = false })).Ok);
        Assert.True((await c1.PostAsync("/api/shifts/movement", new { shiftId = shift, amount = 50m, reason = "Pago de flete", isOut = true })).Ok);
        var nomotivo = await c1.PostAsync("/api/shifts/movement", new { shiftId = shift, amount = 10m, reason = "", isOut = true });
        Assert.Equal("reason_required", nomotivo.Code);
        var tooMuch = await c1.PostAsync("/api/shifts/movement", new { shiftId = shift, amount = 99999m, reason = "x", isOut = true });
        Assert.Equal("insufficient_cash", tooMuch.Code);

        var sum = await c1.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(1000m + 75m + 200m - 50m, sum["expectedCash"].GetDecimal());   // 1225
        Assert.Equal(100m, sum["cardSales"].GetDecimal());

        // Diferencia grande sin nota → se exige explicación
        var noNotes = await c1.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = 1000m });
        Assert.Equal("notes_required", noNotes.Code);
        // Otro cajero no puede cerrar este turno
        var c2 = await Api.LoginAsync(f, "cajero2");
        Assert.Equal(403, (int)(await c2.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = 1225m })).Status);

        var close = await c1.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = 1215m, denominations = new Dictionary<string, int> { ["1000"] = 1, ["200"] = 1, ["10"] = 1, ["5"] = 1 } });
        Assert.True(close.Ok, close.Body.ToString());
        Assert.Equal(1225m, close["expectedCash"].GetDecimal()); Assert.Equal(1215m, close["countedCash"].GetDecimal());
        Assert.Equal(-10m, close["cashDifference"].GetDecimal()); Assert.Equal("Closed", close["status"].GetString());
        var again = await c1.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = 1215m });
        Assert.Equal("shift_closed", again.Code);
        var report = await admin.GetAsync("/api/shifts");
        Assert.Contains(report.Body.EnumerateArray(), s => s.GetProperty("id").GetGuid() == shift && s.GetProperty("cashDifference").GetDecimal() == -10m);
    }
}
