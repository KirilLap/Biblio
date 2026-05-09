using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    public static class OperatorApi
    {
        private static readonly ConcurrentDictionary<string, (string OperatorId, DateTime Expires)> _tokens = new();

        public static bool ValidateToken(string token, out string operatorId)
        {
            operatorId = "";
            if (!_tokens.TryGetValue(token, out var entry)) return false;
            if (entry.Expires < DateTime.UtcNow) { _tokens.TryRemove(token, out _); return false; }
            operatorId = entry.OperatorId;
            return true;
        }

        public static async Task HandleLogin(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(body);
                var login = data.GetProperty("login").GetString() ?? "";
                var password = data.GetProperty("password").GetString() ?? "";
                var hash = HashPassword(password);

                var settings = GlobalSettings.Load();
                var op = settings.Operators.Find(o => o.Login == login && o.PasswordHash == hash && o.IsActive);
                if (op == null)
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Неверный логин или пароль" }));
                    return;
                }

                var token = Guid.NewGuid().ToString("N");
                _tokens[token] = (op.Id, DateTime.UtcNow.AddHours(12));
                ctx.Response.Cookies.Append("bib_op", token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Expires = DateTimeOffset.UtcNow.AddHours(12)
                });
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { id = op.Id, displayName = op.DisplayName, login = op.Login }));
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        public static async Task HandleLogout(HttpContext ctx)
        {
            var token = ctx.Request.Cookies["bib_op"];
            if (token != null) _tokens.TryRemove(token, out _);
            ctx.Response.Cookies.Delete("bib_op");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{}");
        }

        public static async Task HandleMe(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            var token = ctx.Request.Cookies["bib_op"];
            if (token == null || !ValidateToken(token, out var operatorId))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{}");
                return;
            }
            var settings = GlobalSettings.Load();
            var op = settings.Operators.Find(o => o.Id == operatorId);
            if (op == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("{}"); return; }
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { id = op.Id, displayName = op.DisplayName, login = op.Login }));
        }

        public static string HashPassword(string password)
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
