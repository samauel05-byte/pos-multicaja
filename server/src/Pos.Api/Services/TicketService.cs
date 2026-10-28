using System.Text;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public record TicketDto(string Text, string EscPosBase64, int Columns, bool IsCopy, int PrintCount, Guid PrintJobId);

/// <summary>
/// Genera el ticket (texto monoespaciado y bytes ESC/POS). Imprimir NO modifica la venta:
/// solo se registra un PrintJob. Una falla de impresión nunca revierte ni cancela una venta confirmada.
/// </summary>
public class TicketService(AppDb db, SaleService sales, CurrentUser user, AuditService audit)
{
    public async Task<TicketDto> BuildAsync(Guid saleId, bool reprint, int columns = 42)
    {
        var s = await sales.GetDtoAsync(saleId);
        var company = await db.Companies.AsNoTracking().FirstAsync();
        var branch = await db.Branches.AsNoTracking().FirstAsync();

        var sale = await db.Sales.FirstAsync(x => x.Id == saleId);
        var isCopy = reprint || sale.PrintCount > 0;
        // El contador y el registro de impresión se guardan aparte; no tocan importes, inventario ni caja.
        var job = new PrintJob { SaleId = saleId, RegisterId = s.RegisterId, UserId = user.Id, IsReprint = isCopy };
        db.PrintJobs.Add(job);
        sale.PrintCount += 1;
        if (isCopy) audit.Log("ticket.reprint", "sale", saleId, s.Number);
        await db.SaveChangesAsync();

        var text = Render(s, company, branch, columns, isCopy);
        return new TicketDto(text, Convert.ToBase64String(EscPos(text, isCopy)), columns, isCopy, sale.PrintCount, job.Id);
    }

    public async Task MarkAsync(Guid jobId, bool ok, string? error)
    {
        var j = await db.PrintJobs.FirstOrDefaultAsync(x => x.Id == jobId) ?? throw ApiException.NotFound("Trabajo de impresión no encontrado.");
        j.Status = ok ? PrintStatus.Printed : PrintStatus.Failed;
        j.Error = error;
        await db.SaveChangesAsync();
    }

    static string Money2(decimal v) => v.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    public static string Render(SaleDto s, Company c, Branch b, int w, bool isCopy)
    {
        var sb = new StringBuilder();
        string Center(string t) => t.Length >= w ? t[..w] : new string(' ', (w - t.Length) / 2) + t;
        string Row(string l, string r) { var sp = Math.Max(1, w - l.Length - r.Length); return (l.Length + r.Length + 1 > w ? l[..Math.Max(0, w - r.Length - 1)] : l) + new string(' ', sp) + r; }
        var sym = s.CurrencySymbol;
        sb.AppendLine(Center(c.Name));
        if (!string.IsNullOrWhiteSpace(c.TaxId)) sb.AppendLine(Center("RNC " + c.TaxId));
        sb.AppendLine(Center(b.Name));
        if (!string.IsNullOrWhiteSpace(b.Address)) sb.AppendLine(Center(b.Address));
        sb.AppendLine(new string('-', w));
        sb.AppendLine(Row("Ticket:", s.Number));
        sb.AppendLine(Row("Fecha:", s.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")));
        sb.AppendLine(Row("Caja:", s.RegisterName));
        sb.AppendLine(Row("Cajero:", s.CashierName));
        if (s.CustomerName is not null) sb.AppendLine(Row("Cliente:", s.CustomerName));
        if (isCopy) sb.AppendLine(Center("*** COPIA / REIMPRESION ***"));
        if (s.Status == SaleStatus.Voided) sb.AppendLine(Center("*** ANULADA ***"));
        sb.AppendLine(new string('-', w));
        foreach (var l in s.Lines)
        {
            sb.AppendLine(l.ProductName.Length > w ? l.ProductName[..w] : l.ProductName);
            sb.AppendLine(Row($"  {l.Quantity:0.####} x {Money2(l.UnitPrice)}", Money2(l.Quantity * l.UnitPrice)));
            if (l.DiscountAmount > 0) sb.AppendLine(Row($"  Desc. {l.DiscountPercent:0.##}%", "-" + Money2(l.DiscountAmount)));
        }
        sb.AppendLine(new string('-', w));
        sb.AppendLine(Row("Subtotal", $"{sym} {Money2(s.Subtotal)}"));
        if (s.DiscountTotal > 0) sb.AppendLine(Row("Descuentos", $"-{sym} {Money2(s.DiscountTotal)}"));
        sb.AppendLine(Row("Impuestos", $"{sym} {Money2(s.TaxTotal)}"));
        sb.AppendLine(Row("TOTAL", $"{sym} {Money2(s.Total)}"));
        sb.AppendLine(new string('-', w));
        foreach (var p in s.Payments)
        {
            var name = p.Method switch { PayMethod.Cash => "Efectivo", PayMethod.Card => "Tarjeta", PayMethod.Transfer => "Transferencia", _ => "Credito" };
            sb.AppendLine(Row(name + (p.Method == PayMethod.Cash && s.ChangeGiven > 0 ? " (recibido)" : ""), $"{sym} {Money2(p.Method == PayMethod.Cash ? p.Tendered : p.Amount)}"));
        }
        if (s.ChangeGiven > 0) sb.AppendLine(Row("Cambio", $"{sym} {Money2(s.ChangeGiven)}"));
        if (s.CreditAmount > 0 && s.DueDate is not null) sb.AppendLine(Row("Vence", s.DueDate.Value.ToLocalTime().ToString("dd/MM/yyyy")));
        sb.AppendLine(new string('-', w));
        if (!c.FiscalModuleEnabled) sb.AppendLine(Center("DOCUMENTO NO FISCAL"));
        if (!string.IsNullOrWhiteSpace(c.TicketFooter)) sb.AppendLine(Center(c.TicketFooter));
        return sb.ToString();
    }

    /// <summary>Comandos ESC/POS básicos (init, texto CP858, corte parcial). SIN VALIDAR en impresoras físicas.</summary>
    public static byte[] EscPos(string text, bool isCopy)
    {
        var enc = CodePagesEncodingProviderFallback();
        var ms = new MemoryStream();
        ms.Write([0x1B, 0x40]);            // ESC @ inicializar
        ms.Write([0x1B, 0x74, 0x13]);      // ESC t 19 → CP858 (latín con €)
        ms.Write(enc.GetBytes(text.Replace("\r\n", "\n")));
        ms.Write([0x0A, 0x0A, 0x0A]);
        ms.Write([0x1D, 0x56, 0x42, 0x00]); // GS V 66 0 → corte parcial
        return ms.ToArray();
    }

    static Encoding CodePagesEncodingProviderFallback()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(858); }
        catch { return Encoding.ASCII; }
    }
}
