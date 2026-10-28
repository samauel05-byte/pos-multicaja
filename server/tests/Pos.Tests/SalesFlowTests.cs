namespace Pos.Tests;

public class SalesFlowTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Precio_e_impuesto_los_calcula_el_servidor_y_se_conservan_en_la_venta()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();

        // El cliente intenta imponer un precio: el campo se ignora.
        var r = await c1.PostAsync("/api/sales", new { operationKey = Guid.NewGuid(), registerId = reg,
            lines = new[] { new { productId = pid, quantity = 3m, unitPrice = 1m, total = 1m } }, payments = new[] { Seeds.Cash(100) } });
        Assert.True(r.Ok, r.Body.ToString());
        var s = r["sale"];
        Assert.Equal(75m, s.GetProperty("total").GetDecimal());
        Assert.Equal(63.56m, s.GetProperty("subtotal").GetDecimal());
        Assert.Equal(11.44m, s.GetProperty("taxTotal").GetDecimal());
        Assert.Equal(25m, s.GetProperty("changeGiven").GetDecimal());

        // Cambiar el precio del producto no altera la venta histórica.
        var p = await Seeds.Product(admin, "BEB-001");
        var upd = await admin.PutAsync($"/api/products/{pid}", new { sku = "BEB-001", barcode = p.GetProperty("barcode").GetString(), name = "Agua 500 ml", categoryId = p.GetProperty("categoryId").GetGuid(),
            unitId = p.GetProperty("unitId").GetGuid(), taxRateId = p.GetProperty("taxRateId").GetGuid(), cost = 12m, price = 40m, minStock = 24m, tracksStock = true, active = true });
        Assert.True(upd.Ok, upd.Body.ToString());
        var again = await c1.GetAsync($"/api/sales/{s.GetProperty("id").GetGuid()}");
        Assert.Equal(25m, again["lines"][0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(75m, again["total"].GetDecimal());
        // y se restablece para otros tests
        await admin.PutAsync($"/api/products/{pid}", new { sku = "BEB-001", barcode = p.GetProperty("barcode").GetString(), name = "Agua 500 ml", categoryId = p.GetProperty("categoryId").GetGuid(),
            unitId = p.GetProperty("unitId").GetGuid(), taxRateId = p.GetProperty("taxRateId").GetGuid(), cost = 12m, price = 25m, minStock = 24m, tracksStock = true, active = true });
    }

    [Fact]
    public async Task Pago_combinado_efectivo_tarjeta_transferencia_y_cuadre_por_medio()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 1000m);
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var cola = (await Seeds.Product(admin, "BEB-002")).GetProperty("id").GetGuid();     // 50
        var jugo = (await Seeds.Product(admin, "BEB-003")).GetProperty("id").GetGuid();     // 120

        // 2×50 + 1×120 = 220  → tarjeta 100 + transferencia 50 + efectivo 70 (entrega 100, cambio 30)
        var r = await Seeds.Sell(c1, reg, [Seeds.Line(cola, 2), Seeds.Line(jugo, 1)],
            [Seeds.Pay("Card", 100, "4242"), Seeds.Pay("Transfer", 50, "TRX-1"), Seeds.Cash(100)]);
        Assert.True(r.Ok, r.Body.ToString());
        var sale = r["sale"];
        Assert.Equal(220m, sale.GetProperty("total").GetDecimal());
        Assert.Equal(30m, sale.GetProperty("changeGiven").GetDecimal());
        Assert.Equal(3, sale.GetProperty("payments").GetArrayLength());

        var sum = await c1.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(70m, sum["cashSales"].GetDecimal());          // solo lo aplicado en efectivo, sin el cambio
        Assert.Equal(100m, sum["cardSales"].GetDecimal());
        Assert.Equal(50m, sum["transferSales"].GetDecimal());
        Assert.Equal(1070m, sum["expectedCash"].GetDecimal());     // tarjeta/transferencia NO entran a la gaveta

        // Falta dinero / sobrepago no efectivo / efectivo innecesario
        var short1 = await Seeds.Sell(c1, reg, [Seeds.Line(cola, 1)], [Seeds.Cash(40)]);
        Assert.Equal("insufficient_payment", short1.Code);
        var over = await Seeds.Sell(c1, reg, [Seeds.Line(cola, 1)], [Seeds.Pay("Card", 80)]);
        Assert.Equal("overpayment", over.Code);
        var needless = await Seeds.Sell(c1, reg, [Seeds.Line(cola, 1)], [Seeds.Pay("Card", 50), Seeds.Cash(10)]);
        Assert.Equal("cash_not_needed", needless.Code);
    }

    [Fact]
    public async Task Venta_a_credito_limite_saldo_vencimiento_y_abonos()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1", 500m);
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var jugo = (await Seeds.Product(admin, "BEB-003")).GetProperty("id").GetGuid();
        var custs = (await c1.GetAsync("/api/customers")).Body.EnumerateArray().ToList();
        var maria = custs.First(c => c.GetProperty("name").GetString()!.StartsWith("María")).GetProperty("id").GetGuid();     // límite 5000, 15 días
        var sinCredito = custs.First(c => c.GetProperty("name").GetString()!.StartsWith("Juan")).GetProperty("id").GetGuid();

        var noCust = await Seeds.Sell(c1, reg, [Seeds.Line(jugo, 1)], [Seeds.Pay("Credit", 120)]);
        Assert.Equal("customer_required", noCust.Code);
        var over = await Seeds.Sell(c1, reg, [Seeds.Line(jugo, 1)], [Seeds.Pay("Credit", 120)], customer: sinCredito);
        Assert.Equal("credit_limit_exceeded", over.Code);

        // 2 jugos = 240: 40 en efectivo + 200 a crédito
        var ok = await Seeds.Sell(c1, reg, [Seeds.Line(jugo, 2)], [Seeds.Pay("Credit", 200), Seeds.Cash(40)], customer: maria);
        Assert.True(ok.Ok, ok.Body.ToString());
        var sale = ok["sale"];
        Assert.Equal(200m, sale.GetProperty("creditBalance").GetDecimal());
        var due = sale.GetProperty("dueDate").GetDateTime();
        Assert.InRange((due - DateTime.UtcNow).TotalDays, 14.9, 15.1);
        var st = await c1.GetAsync($"/api/customers/{maria}/statement");
        Assert.Equal(200m, st["customer"].GetProperty("balance").GetDecimal());

        // Abono en efectivo de 80 entra a la caja; excederse del saldo se rechaza
        var key = Guid.NewGuid();
        var ab = await c1.PostAsync("/api/customers/payments", new { operationKey = key, customerId = maria, registerId = reg, method = "Cash", amount = 80m });
        Assert.True(ab.Ok, ab.Body.ToString());
        Assert.Equal(120m, ab["newBalance"].GetDecimal());
        var ab2 = await c1.PostAsync("/api/customers/payments", new { operationKey = key, customerId = maria, registerId = reg, method = "Cash", amount = 80m });   // reintento
        Assert.True(ab2["duplicate"].GetBoolean()); Assert.Equal(120m, ab2["newBalance"].GetDecimal());
        var excess = await c1.PostAsync("/api/customers/payments", new { operationKey = Guid.NewGuid(), customerId = maria, registerId = reg, method = "Cash", amount = 500m });
        Assert.Equal("overpayment", excess.Code);

        var sum = await c1.GetAsync($"/api/shifts/{shift}/summary");
        Assert.Equal(500m + 40m + 80m, sum["expectedCash"].GetDecimal());   // fondo + efectivo de venta + abono
        Assert.Equal(200m, sum["creditSales"].GetDecimal());
    }

    [Fact]
    public async Task Descuento_sobre_el_limite_del_cajero_exige_pin_de_supervisor()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "DES-003")).GetProperty("id").GetGuid();      // 210

        var small = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 5)], [Seeds.Cash(500)]);   // 5% = tope del cajero
        Assert.True(small.Ok, small.Body.ToString());
        Assert.Equal(199.5m, small["sale"].GetProperty("total").GetDecimal());

        var noAuth = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 10)], [Seeds.Cash(500)]);
        Assert.Equal(403, (int)noAuth.Status); Assert.Equal("authorization_required", noAuth.Code);
        var badPin = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 10)], [Seeds.Cash(500)], authUser: "supervisor", authPin: "9999");
        Assert.Equal("authorization_invalid", badPin.Code);
        var cashierAsAuth = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 10)], [Seeds.Cash(500)], authUser: "cajero2", authPin: "1234");
        Assert.Equal("authorization_invalid", cashierAsAuth.Code);
        var good = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 10)], [Seeds.Cash(500)], authUser: "supervisor", authPin: "1234");
        Assert.True(good.Ok, good.Body.ToString());
        Assert.Equal(189m, good["sale"].GetProperty("total").GetDecimal());
        var tooMuch = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1, 50)], [Seeds.Cash(500)], authUser: "supervisor", authPin: "1234");
        Assert.Equal("discount_over_limit", tooMuch.Code);

        var audit = await admin.GetAsync("/api/audit?action=authorization");
        Assert.Contains(audit.Body.EnumerateArray(), a => a.GetProperty("action").GetString() == "authorization.failed");
        Assert.Contains(audit.Body.EnumerateArray(), a => a.GetProperty("action").GetString() == "authorization.granted");
    }

    [Fact]
    public async Task Cantidades_decimales_solo_en_unidades_que_lo_permiten_y_sin_turno_no_se_cobra()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); var shift = await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var arroz = (await Seeds.Product(admin, "DES-001")).GetProperty("id").GetGuid();    // libra, 32, exento
        var agua = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();

        var dec = await Seeds.Sell(c1, reg, [Seeds.Line(arroz, 1.5m)], [Seeds.Cash(100)]);
        Assert.True(dec.Ok, dec.Body.ToString());
        Assert.Equal(48m, dec["sale"].GetProperty("total").GetDecimal());
        Assert.Equal(0m, dec["sale"].GetProperty("taxTotal").GetDecimal());
        var bad = await Seeds.Sell(c1, reg, [Seeds.Line(agua, 1.5m)], [Seeds.Cash(100)]);
        Assert.Equal("quantity_not_integer", bad.Code);

        var sum = await c1.GetAsync($"/api/shifts/{shift}/summary");
        Assert.True((await c1.PostAsync("/api/shifts/close", new { shiftId = shift, countedCash = sum["expectedCash"].GetDecimal() })).Ok);
        var after = await Seeds.Sell(c1, reg, [Seeds.Line(agua, 1)], [Seeds.Cash(100)]);
        Assert.Equal(409, (int)after.Status); Assert.Equal("no_open_shift", after.Code);
    }

    [Fact]
    public async Task Cotizacion_se_convierte_en_venta_una_sola_vez()
    {
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var admin = await Api.AdminAsync(f); var reg = await Seeds.RegisterId(admin, "Caja 1");
        var pid = (await Seeds.Product(admin, "LIM-001")).GetProperty("id").GetGuid();   // 150
        var q = await c1.PostAsync("/api/quotes", new { customerId = (Guid?)null, validDays = 7, notes = "prueba", lines = new[] { new { productId = pid, quantity = 2m } } });
        Assert.True(q.Ok, q.Body.ToString());
        var qid = q["id"].GetGuid();
        Assert.Equal(300m, q["total"].GetDecimal());

        var sale = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(300)], quote: qid);
        Assert.True(sale.Ok, sale.Body.ToString());
        var got = await c1.GetAsync($"/api/quotes/{qid}");
        Assert.Equal("Converted", got["status"].GetString());
        var again = await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(300)], quote: qid);
        Assert.Equal("quote_not_open", again.Code);
    }
}
