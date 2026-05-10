using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    public static class AdminAuth
    {
        private static readonly ConcurrentDictionary<string, DateTime> _tokens = new();

        public static bool IsValidToken(string token)
        {
            if (!_tokens.TryGetValue(token, out var exp)) return false;
            if (exp < DateTime.UtcNow) { _tokens.TryRemove(token, out _); return false; }
            return true;
        }

        public static bool IsAuthorized(HttpContext ctx)
        {
            var token = ctx.Request.Cookies["bib_admin"];
            return token != null && IsValidToken(token);
        }

        public static async Task HandleLogin(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(body);
                var password = data.GetProperty("password").GetString() ?? "";
                var settings = GlobalSettings.Load();
                if (GlobalSettings.HashPassword(password) != settings.AdminPasswordHash)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Неверный пароль" }));
                    return;
                }
                var token = Guid.NewGuid().ToString("N");
                _tokens[token] = DateTime.UtcNow.AddHours(24);
                ctx.Response.Cookies.Append("bib_admin", token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddHours(24)
                });
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true }));
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        public static Task HandleLogout(HttpContext ctx)
        {
            var token = ctx.Request.Cookies["bib_admin"];
            if (token != null) _tokens.TryRemove(token, out _);
            ctx.Response.Cookies.Delete("bib_admin");
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync("{}");
        }

        public static async Task HandleCheck(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            if (!IsAuthorized(ctx)) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{}"); return; }
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true }));
        }
    }
}
