using Pos.Api.Services;

namespace Pos.Api;

/// <summary>
/// Uso: Pos.Api --restore &lt;archivo.posbak&gt; --target-db &lt;nombre&gt; [--replace]
/// La frase de paso se toma de Backup__Passphrase. Con --replace se elimina y recrea la base destino.
/// </summary>
public static class CliRestore
{
    public static async Task<int> RunAsync(WebApplication app, string[] a)
    {
        string? Arg(string n) { var i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        var file = Arg("--restore"); var target = Arg("--target-db");
        if (file is null || target is null) { Console.Error.WriteLine("Uso: --restore <archivo.posbak> --target-db <nombre> [--replace]"); return 2; }
        var pass = app.Configuration["Backup:Passphrase"];
        if (string.IsNullOrWhiteSpace(pass)) { Console.Error.WriteLine("Defina Backup__Passphrase."); return 2; }
        try
        {
            await app.Services.GetRequiredService<BackupService>().RestoreAsync(file, pass, target, a.Contains("--replace"));
            Console.WriteLine($"Restauración completada en la base '{target}'.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }
}
