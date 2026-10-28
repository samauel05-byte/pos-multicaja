using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public class StockService(AppDb db)
{
    /// <summary>
    /// Descuento atómico: UPDATE condicional con RETURNING. Si dos cajas venden la última unidad,
    /// PostgreSQL serializa por fila y solo una cumple "Quantity >= q". No hay lectura-modificación-escritura en la app.
    /// Debe llamarse dentro de una transacción.
    /// </summary>
    public async Task<decimal> DecreaseAsync(Guid productId, Guid locationId, decimal qty, bool allowNegative, string productName)
    {
        var res = await ExecAsync(
            "UPDATE \"StockLevels\" SET \"Quantity\" = \"Quantity\" - @q WHERE \"ProductId\"=@p AND \"LocationId\"=@l AND (@neg OR \"Quantity\" >= @q) RETURNING \"Quantity\"",
            productId, locationId, qty, allowNegative);
        if (res is null)
        {
            var have = await db.StockLevels.AsNoTracking().Where(s => s.ProductId == productId && s.LocationId == locationId).Select(s => (decimal?)s.Quantity).FirstOrDefaultAsync() ?? 0m;
            throw ApiException.Conflict("insufficient_stock", $"Existencia insuficiente de \"{productName}\" (disponible: {have:0.####}).");
        }
        return res.Value;
    }

    public async Task<decimal> IncreaseAsync(Guid productId, Guid locationId, decimal qty)
    {
        var o = await db.ScalarAsync<decimal?>(
            "INSERT INTO \"StockLevels\" (\"ProductId\",\"LocationId\",\"Quantity\") VALUES (@p,@l,@q) ON CONFLICT (\"ProductId\",\"LocationId\") DO UPDATE SET \"Quantity\" = \"StockLevels\".\"Quantity\" + @q RETURNING \"Quantity\"",
            new NpgsqlParameter("p", productId), new NpgsqlParameter("l", locationId), new NpgsqlParameter("q", qty));
        return o ?? 0;
    }

    async Task<decimal?> ExecAsync(string sql, Guid p, Guid l, decimal q, bool neg)
    {
        return await db.ScalarAsync<decimal?>(sql,
            new NpgsqlParameter("p", p), new NpgsqlParameter("l", l), new NpgsqlParameter("q", q), new NpgsqlParameter("neg", neg));
    }

    public void Kardex(Guid productId, Guid locationId, MovementType type, decimal signedQty, decimal balanceAfter, decimal unitCost,
        string? reference, Guid? documentId, string? reason, Guid? userId)
    {
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = productId, LocationId = locationId, Type = type, Quantity = signedQty, BalanceAfter = balanceAfter,
            UnitCost = unitCost, Reference = reference, DocumentId = documentId, Reason = reason, UserId = userId,
        });
    }
}
