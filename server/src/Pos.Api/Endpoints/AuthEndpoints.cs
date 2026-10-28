using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pos.Api.Data;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Endpoints;

public class TokenSigner(string key)
{
    public string Issue(AppUser u)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var t = new JwtSecurityToken(claims: [new("uid", u.Id.ToString()), new("name", u.Username), new("role", u.Role.ToString())],
            expires: DateTime.UtcNow.AddHours(12), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(t);
    }
}

public record LoginReq(string Username, string Password);

public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", async (LoginReq r, AppDb db, TokenSigner signer, AuditService audit) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Username == r.Username.Trim().ToLower());
            if (u is not null && u.LockedUntil > DateTime.UtcNow)
                throw new ApiException(429, "locked", "Usuario bloqueado temporalmente por intentos fallidos. Intente en unos minutos.");
            if (u is null || !u.Active || !PasswordHasher.Verify(r.Password ?? "", u.PasswordHash))
            {
                if (u is not null)
                {
                    u.FailedLogins++;
                    if (u.FailedLogins >= 5) { u.LockedUntil = DateTime.UtcNow.AddMinutes(5); u.FailedLogins = 0; }
                }
                await audit.LogNowAsync("login.failed", null, r.Username);
                throw new ApiException(401, "invalid_credentials", "Usuario o contraseña incorrectos.");
            }
            u.FailedLogins = 0; u.LockedUntil = null;
            await audit.LogNowAsync("login.ok", null, u.Username);
            return new { token = signer.Issue(u), user = new { u.Id, u.Username, u.FullName, role = u.Role.ToString(), permissions = Perm.For(u.Role) } };
        });

        var g = api.MapGroup("").RequireAuthorization();
        g.MapGet("/auth/me", async (CurrentUser cu, AppDb db) =>
        {
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == cu.Id && x.Active) ?? throw new ApiException(401, "session_invalid", "Sesión inválida.");
            return new { u.Id, u.Username, u.FullName, role = u.Role.ToString(), permissions = Perm.For(u.Role) };
        });
    }
}
