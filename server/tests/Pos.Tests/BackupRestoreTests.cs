using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Api.Services;

namespace Pos.Tests;

public class BackupRestoreTests(PosFixture f) : IClassFixture<PosFixture>
{
    [Fact]
    public async Task Respaldo_cifrado_se_restaura_en_otra_base_con_los_mismos_datos()
    {
        var admin = await Api.AdminAsync(f);
        var c1 = await Api.LoginAsync(f, "cajero1"); await Seeds.FreshShift(f, c1, "Caja 1");
        var reg = await Seeds.RegisterId(admin, "Caja 1"); var pid = (await Seeds.Product(admin, "BEB-001")).GetProperty("id").GetGuid();
        Assert.True((await Seeds.Sell(c1, reg, [Seeds.Line(pid, 2)], [Seeds.Cash(50)])).Ok);

        var salesAtBackup = await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Sales\"");
        var stockAtBackup = await Seeds.Stock(admin, "BEB-001");
        var bk = await admin.PostAsync("/api/backups");
        Assert.True(bk.Ok, bk.Body.ToString());
        var file = Path.Combine(f.BackupDir, bk["fileName"].GetString()!);
        Assert.True(File.Exists(file));

        // El archivo está cifrado: no contiene texto legible de la base
        var raw = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain("PGDMP", System.Text.Encoding.ASCII.GetString(raw));
        Assert.DoesNotContain("Comercial Demo", System.Text.Encoding.UTF8.GetString(raw));

        // Cambios posteriores al respaldo (se perderán al restaurar: es lo esperado)
        Assert.True((await Seeds.Sell(c1, reg, [Seeds.Line(pid, 1)], [Seeds.Cash(50)])).Ok);

        var restoreDb = f.DbName + "_restored";
        var svc = f.Factory.Services.GetRequiredService<BackupService>();
        await svc.RestoreAsync(file, PosFixture.BackupPass, restoreDb, dropIfExists: true);
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(f.ConnString) { Database = restoreDb }.ConnectionString;
            Assert.Equal(salesAtBackup, await Sql.Scalar<long>(cs, "select count(*) from \"Sales\""));
            Assert.Equal(stockAtBackup, await Sql.Scalar<decimal>(cs, $"select \"Quantity\" from \"StockLevels\" where \"ProductId\"='{pid}'"));
            Assert.Equal(await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Products\""), await Sql.Scalar<long>(cs, "select count(*) from \"Products\""));
            Assert.Equal(await Sql.Scalar<long>(f.ConnString, "select count(*) from \"__EFMigrationsHistory\""), await Sql.Scalar<long>(cs, "select count(*) from \"__EFMigrationsHistory\""));
            Assert.True(await Sql.Scalar<long>(f.ConnString, "select count(*) from \"Sales\"") > salesAtBackup);   // la base viva sí tiene la venta posterior
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(PosFixture.AdminConn); await c.OpenAsync();
            await using var d = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{restoreDb}\" WITH (FORCE)", c); await d.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Frase_incorrecta_archivo_alterado_o_truncado_se_rechazan()
    {
        var admin = await Api.AdminAsync(f);
        var bk = await admin.PostAsync("/api/backups"); Assert.True(bk.Ok, bk.Body.ToString());
        var file = Path.Combine(f.BackupDir, bk["fileName"].GetString()!);
        var svc = f.Factory.Services.GetRequiredService<BackupService>();
        var db = f.DbName + "_bad";

        await Assert.ThrowsAsync<InvalidDataException>(() => svc.RestoreAsync(file, "frase-equivocada-999", db, true));

        var bytes = await File.ReadAllBytesAsync(file);
        var tampered = Path.Combine(f.BackupDir, "tampered.posbak");
        var t = (byte[])bytes.Clone(); t[t.Length / 2] ^= 0xFF; await File.WriteAllBytesAsync(tampered, t);
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.RestoreAsync(tampered, PosFixture.BackupPass, db, true));

        var truncated = Path.Combine(f.BackupDir, "truncated.posbak");
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length - 40)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.RestoreAsync(truncated, PosFixture.BackupPass, db, true));

        // Ninguna de las pruebas dejó una base a medias
        await using var c = new NpgsqlConnection(PosFixture.AdminConn); await c.OpenAsync();
        await using var q = new NpgsqlCommand($"select count(*) from pg_database where datname='{db}'", c);
        Assert.Equal(0L, (long)(await q.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Cifrado_por_bloques_round_trip_con_archivos_de_distintos_tamanos()
    {
        foreach (var size in new[] { 0, 1, 1_048_575, 1_048_576, 1_048_577, 3_500_000 })
        {
            var src = Path.GetTempFileName(); var enc = src + ".enc"; var dec = src + ".dec";
            var data = new byte[size]; Random.Shared.NextBytes(data); await File.WriteAllBytesAsync(src, data);
            await BackupService.EncryptFileAsync(src, enc, "una-frase-larga");
            await BackupService.DecryptFileAsync(enc, dec, "una-frase-larga");
            Assert.Equal(data, await File.ReadAllBytesAsync(dec));
            foreach (var x in new[] { src, enc, dec }) File.Delete(x);
        }
    }
}
