using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Api.Data;
using Pos.Api.Domain;

namespace Pos.Api.Services;

public class BackupOptions
{
    public string Directory { get; set; } = "backups";
    public string? Passphrase { get; set; }
    public string PgBinDir { get; set; } = "";     // carpeta con pg_dump / pg_restore (vacío = en PATH)
    public bool AutoEnabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int KeepLast { get; set; } = 14;
    public int FirstRunDelaySeconds { get; set; } = 120;
}

/// <summary>
/// Respaldo = pg_dump (formato custom) → cifrado AES-256-GCM por bloques con clave derivada (PBKDF2) de la frase de paso.
/// El archivo .posbak es ilegible sin la frase y detecta alteraciones o truncamiento.
/// </summary>
public class BackupService(IConfiguration cfg, ILogger<BackupService> log, IServiceScopeFactory scopes)
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("POSBAK01");
    const int Chunk = 1 << 20, Iter = 210_000;

    public BackupOptions Options
    {
        get
        {
            var o = cfg.GetSection("Backup").Get<BackupOptions>() ?? new();
            // Como servicio de Windows el directorio actual es System32: las rutas relativas se resuelven junto a la aplicación.
            if (!Path.IsPathRooted(o.Directory)) o.Directory = Path.Combine(AppContext.BaseDirectory, o.Directory);
            return o;
        }
    }

    string Tool(string name)
    {
        var dir = Options.PgBinDir;
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return string.IsNullOrWhiteSpace(dir) ? exe : Path.Combine(dir, exe);
    }

    static async Task<(int Code, string Err)> RunAsync(string exe, IEnumerable<string> args, NpgsqlConnectionStringBuilder cs)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PGPASSWORD"] = cs.Password ?? "";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}");
        var err = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await err);
    }

    static string[] Conn(NpgsqlConnectionStringBuilder cs, string db) =>
        ["-h", cs.Host ?? "localhost", "-p", cs.Port.ToString(), "-U", cs.Username ?? "postgres", "-d", db];

    public async Task<BackupRecord> CreateAsync(string kind)
    {
        var opt = Options;
        if (string.IsNullOrWhiteSpace(opt.Passphrase) || opt.Passphrase.Length < 8)
            throw ApiException.Bad("backup_passphrase", "Configure POS_BACKUP_PASSPHRASE (mínimo 8 caracteres) para proteger los respaldos.");
        System.IO.Directory.CreateDirectory(opt.Directory);
        var cs = new NpgsqlConnectionStringBuilder(cfg.GetConnectionString("Db"));
        var name = $"pos-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}.posbak";
        var final = Path.Combine(opt.Directory, name);
        var tmp = Path.Combine(Path.GetTempPath(), $"posdump-{Guid.NewGuid():N}.dump");
        var rec = new BackupRecord { FileName = name, Kind = kind };
        try
        {
            var (code, err) = await RunAsync(Tool("pg_dump"), ["-Fc", "-f", tmp, .. Conn(cs, cs.Database!)], cs);
            if (code != 0) throw new InvalidOperationException("pg_dump falló: " + err);
            // Verificación: el volcado debe poder listarse antes de darlo por bueno.
            var (c2, e2) = await RunAsync(Tool("pg_restore"), ["--list", tmp], cs);
            if (c2 != 0) throw new InvalidOperationException("El volcado no es legible: " + e2);
            await EncryptFileAsync(tmp, final, opt.Passphrase);
            rec.SizeBytes = new FileInfo(final).Length;
            rec.Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(final)));
            rec.Success = true;
            Prune(opt);
        }
        catch (Exception ex)
        {
            rec.Success = false; rec.Error = ex.Message;
            if (File.Exists(final)) File.Delete(final);
            log.LogError(ex, "Respaldo falló");
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Backups.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    void Prune(BackupOptions opt)
    {
        var files = new DirectoryInfo(opt.Directory).GetFiles("pos-*-auto.posbak").OrderByDescending(f => f.CreationTimeUtc).Skip(opt.KeepLast);
        foreach (var f in files) f.Delete();
    }

    /// <summary>Restaura en la base indicada (se recrea). Use una base nueva para verificar sin tocar producción.</summary>
    public async Task RestoreAsync(string file, string passphrase, string targetDb, bool dropIfExists)
    {
        var cs = new NpgsqlConnectionStringBuilder(cfg.GetConnectionString("Db"));
        var tmp = Path.Combine(Path.GetTempPath(), $"posrestore-{Guid.NewGuid():N}.dump");
        try
        {
            await DecryptFileAsync(file, tmp, passphrase);
            var admin = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = "postgres" };
            await using (var c = new NpgsqlConnection(admin.ConnectionString))
            {
                await c.OpenAsync();
                if (dropIfExists)
                {
                    await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @d AND pid <> pg_backend_pid()", c);
                    kill.Parameters.AddWithValue("d", targetDb); await kill.ExecuteNonQueryAsync();
                    await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{targetDb.Replace("\"", "")}\"", c); await drop.ExecuteNonQueryAsync();
                }
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{targetDb.Replace("\"", "")}\"", c); await create.ExecuteNonQueryAsync();
            }
            var (code, err) = await RunAsync(Tool("pg_restore"), ["--no-owner", "--exit-on-error", .. Conn(cs, targetDb), tmp], cs);
            if (code != 0) throw new InvalidOperationException("pg_restore falló: " + err);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ---- Cifrado por bloques AES-256-GCM ----
    public static async Task EncryptFileAsync(string input, string output, string pass)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var prefix = RandomNumberGenerator.GetBytes(4);
        var key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, Iter, HashAlgorithmName.SHA256, 32);
        await using var fin = File.OpenRead(input);
        await using var fout = File.Create(output);
        await fout.WriteAsync(Magic); await fout.WriteAsync(salt); await fout.WriteAsync(prefix);
        await fout.WriteAsync(BitConverter.GetBytes(Chunk)); await fout.WriteAsync(BitConverter.GetBytes(Iter));
        using var gcm = new AesGcm(key, 16);
        var buf = new byte[Chunk]; var next = new byte[Chunk];
        int n = await ReadFullAsync(fin, buf);
        ulong counter = 0;
        do
        {
            int m = n == Chunk ? await ReadFullAsync(fin, next) : 0;
            bool last = m == 0;
            var nonce = Nonce(prefix, counter++);
            var ct = new byte[n]; var tag = new byte[16];
            gcm.Encrypt(nonce, buf.AsSpan(0, n), ct, tag, new[] { (byte)(last ? 1 : 0) });
            await fout.WriteAsync(new[] { (byte)(last ? 1 : 0) }); await fout.WriteAsync(BitConverter.GetBytes(n));
            await fout.WriteAsync(ct); await fout.WriteAsync(tag);
            (buf, next) = (next, buf); n = m;
            if (last) break;
        } while (true);
    }

    public static async Task DecryptFileAsync(string input, string output, string pass)
    {
        await using var fin = File.OpenRead(input);
        var head = new byte[8 + 16 + 4 + 4 + 4];
        if (await ReadFullAsync(fin, head) != head.Length || !head.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Archivo de respaldo inválido.");
        var salt = head[8..24]; var prefix = head[24..28]; var iter = BitConverter.ToInt32(head, 32);
        var key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, iter, HashAlgorithmName.SHA256, 32);
        using var gcm = new AesGcm(key, 16);
        await using var fout = File.Create(output);
        ulong counter = 0; bool sawLast = false;
        var hdr = new byte[5];
        while (await ReadFullAsync(fin, hdr) == 5)
        {
            if (sawLast) throw new InvalidDataException("Datos después del último bloque.");
            bool last = hdr[0] == 1; int len = BitConverter.ToInt32(hdr, 1);
            if (len < 0 || len > Chunk) throw new InvalidDataException("Bloque corrupto.");
            var ct = new byte[len]; var tag = new byte[16];
            if (await ReadFullAsync(fin, ct) != len || await ReadFullAsync(fin, tag) != 16) throw new InvalidDataException("Archivo truncado.");
            var pt = new byte[len];
            try { gcm.Decrypt(Nonce(prefix, counter++), ct, tag, pt, new[] { hdr[0] }); }
            catch (CryptographicException) { throw new InvalidDataException("Frase de paso incorrecta o archivo alterado."); }
            await fout.WriteAsync(pt);
            sawLast = last;
        }
        if (!sawLast) throw new InvalidDataException("Archivo truncado (falta el bloque final).");
    }

    static byte[] Nonce(byte[] prefix, ulong counter)
    {
        var n = new byte[12]; prefix.CopyTo(n, 0); BitConverter.GetBytes(counter).CopyTo(n, 4); return n;
    }

    static async Task<int> ReadFullAsync(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length) { int r = await s.ReadAsync(buf.AsMemory(total)); if (r == 0) break; total += r; }
        return total;
    }
}

public class BackupScheduler(BackupService backups, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = backups.Options;
        if (!o.AutoEnabled || string.IsNullOrWhiteSpace(o.Passphrase)) { log.LogWarning("Respaldos automáticos desactivados (sin frase de paso o desactivados)."); return; }
        // Primer respaldo FirstRunDelaySeconds después de arrancar; luego cada IntervalHours.
        try { await Task.Delay(TimeSpan.FromSeconds(o.FirstRunDelaySeconds), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            var r = await backups.CreateAsync("auto");
            log.LogInformation("Respaldo automático {File}: {Ok}", r.FileName, r.Success ? "OK" : r.Error);
            try { await Task.Delay(TimeSpan.FromHours(o.IntervalHours), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
