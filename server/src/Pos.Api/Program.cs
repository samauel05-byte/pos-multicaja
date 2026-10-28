using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pos.Api;
using Pos.Api.Data;
using Pos.Api.Endpoints;
using Pos.Api.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();   // permite ejecutarlo como Servicio de Windows (no tiene efecto en consola/Linux)

// ---- Configuración por variables de entorno (ConnectionStrings__Db, Jwt__Key, Backup__*, Seed__*) ----
var cfg = builder.Configuration;
var connString = cfg.GetConnectionString("Db") ?? throw new InvalidOperationException("Falta ConnectionStrings__Db");
var jwtKey = cfg["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    Console.WriteLine("[AVISO] Jwt__Key no configurada (mínimo 32 caracteres): se generó una clave temporal; las sesiones se cerrarán al reiniciar el servidor.");
}

builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connString));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AuthorizerService>();
builder.Services.AddScoped<Numbering>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<ShiftService>();
builder.Services.AddScoped<SaleService>();
builder.Services.AddScoped<ReturnService>();
builder.Services.AddScoped<CreditService>();
builder.Services.AddScoped<PurchaseService>();
builder.Services.AddScoped<TicketService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1),
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
    };
});
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});
builder.Services.AddSingleton(new TokenSigner(jwtKey));

var app = builder.Build();

// ---- Modos de línea de comandos: restauración y verificación de respaldos ----
if (args.Length > 0 && args[0] == "--restore") return await CliRestore.RunAsync(app, args);

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.ContentType = "application/json";
    if (ex is ApiException api)
    {
        ctx.Response.StatusCode = api.Status;
        await ctx.Response.WriteAsJsonAsync(new { code = api.Code, message = api.Message });
    }
    else if (ex is DbUpdateException { InnerException: Npgsql.PostgresException pg } && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
    {
        ctx.Response.StatusCode = 409;
        await ctx.Response.WriteAsJsonAsync(new { code = "duplicate", message = "Ya existe un registro con ese valor único." });
    }
    else if (ex is BadHttpRequestException or JsonException)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { code = "bad_request", message = "Solicitud inválida." });
    }
    else
    {
        app.Logger.LogError(ex, "Error no controlado");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { code = "server_error", message = "Error interno del servidor." });
    }
}));

if (cfg.GetValue("Database:MigrateOnStart", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    await Seeder.RunAsync(db, cfg, app.Logger);
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff"; h["X-Frame-Options"] = "DENY"; h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapGet("/health", async (AppDb db) =>
{
    var ok = await db.Database.CanConnectAsync();
    return ok ? Results.Ok(new { status = "ok", time = DateTime.UtcNow }) : Results.Json(new { status = "db_down" }, statusCode: 503);
});
AuthEndpoints.Map(api);
CatalogEndpoints.Map(api);
SalesEndpoints.Map(api);
PeopleEndpoints.Map(api);
ReportEndpoints.Map(api);
AdminEndpoints.Map(api);

api.MapFallback(() => Results.NotFound(new { code = "not_found", message = "Ruta no encontrada." }));
app.MapFallbackToFile("index.html");   // SPA

app.Run();
return 0;

public partial class Program { }
