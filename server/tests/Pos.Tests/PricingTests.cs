using Pos.Api.Services;
namespace Pos.Tests;

public class PricingTests
{
    [Fact]
    public void Precio_con_impuesto_incluido_separa_base_e_impuesto()
    {
        var c = Pricing.Line(3, 25m, null, null, 0.18m, true);
        Assert.Equal((75m, 63.56m, 11.44m), (c.Total, c.Net, c.Tax));
    }

    [Fact]
    public void Precio_sin_impuesto_incluido_suma_el_impuesto_y_aplica_descuento_primero()
    {
        var c = Pricing.Line(1, 100m, 10m, null, 0.18m, false);
        Assert.Equal((10m, 90m, 16.20m, 106.20m), (c.Discount, c.Net, c.Tax, c.Total));
    }

    [Fact]
    public void Redondeo_definido_mitad_se_aleja_de_cero_a_dos_decimales()
    {
        Assert.Equal(0.13m, Money.R(0.125m)); Assert.Equal(-0.13m, Money.R(-0.125m)); Assert.Equal(2.68m, Money.R(2.675m)); Assert.Equal(1.00m, Money.R(0.995m));
    }

    [Fact]
    public void Descuento_por_monto_y_limite_no_mayor_al_importe()
    {
        var c = Pricing.Line(2, 50m, null, 20m, 0m, true);
        Assert.Equal(80m, c.Total); Assert.Equal(20m, c.EffectivePercent);
        Assert.Throws<ApiException>(() => Pricing.Line(1, 10m, null, 11m, 0.18m, true));
    }

    [Fact]
    public void Cantidades_decimales_exactas_sin_error_de_punto_flotante()
    {
        var c = Pricing.Line(0.1m + 0.2m, 10m, null, null, 0m, true);   // en double daría 3.0000000000000004
        Assert.Equal(3.00m, c.Total);
    }
}
