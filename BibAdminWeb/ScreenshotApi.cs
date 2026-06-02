using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    public static class ScreenshotApi
    {
        // Called from ServerHost: handles /api/screenshot/* endpoints.
        // Upload (/api/screenshot/upload) is handled before this (no auth).
        // Watch/Unwatch/Get require admin or operator auth.
        public static async Task Handle(HttpContext ctx, RequestDelegate next)
        {
            var path = ctx.Request.Path.Value ?? "";
            var method = ctx.Request.Method;

            if (!path.StartsWith("/api/screenshot/")) { await next(ctx); return; }

            // Upload — no auth (called by BibClient)
            if (method == "POST" && path == "/api/screenshot/upload")
            {
                var pc = ctx.Request.Query["pc"].ToString();
                if (string.IsNullOrEmpty(pc)) { ctx.Response.StatusCode = 400; return; }
                using var ms = new System.IO.MemoryStream();
                await ctx.Request.Body.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                    ScreenshotStore.StoreFrame(pc, bytes);
                ctx.Response.StatusCode = 204;
                return;
            }

            // All other endpoints require admin or operator auth
            if (!IsAuthorized(ctx))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
                return;
            }

            // GET /api/screenshot/{pc} — return latest JPEG frame
            if (method == "GET" && path.StartsWith("/api/screenshot/") && !path.Contains("/watch") && !path.Contains("/unwatch"))
            {
                var pc = Uri.UnescapeDataString(path["/api/screenshot/".Length..]);
                var (data, _) = ScreenshotStore.GetFrame(pc);
                if (data == null || data.Length == 0) { ctx.Response.StatusCode = 204; return; }
                ctx.Response.ContentType = "image/jpeg";
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store";
                await ctx.Response.Body.WriteAsync(data);
                return;
            }

            // POST /api/screenshot/watch/{pc}
            if (method == "POST" && path.StartsWith("/api/screenshot/") && path.EndsWith("/watch"))
            {
                var pc = Uri.UnescapeDataString(path["/api/screenshot/".Length..^"/watch".Length]);
                var count = ScreenshotStore.AddViewer(pc);
                if (count == 1)
                    await AdminBroadcaster.Instance?.SendCommandToClient(pc, "START_SCREENSHOT_STREAM")!;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($"{{\"viewers\":{count}}}");
                return;
            }

            // POST /api/screenshot/unwatch/{pc}
            if (method == "POST" && path.StartsWith("/api/screenshot/") && path.EndsWith("/unwatch"))
            {
                var pc = Uri.UnescapeDataString(path["/api/screenshot/".Length..^"/unwatch".Length]);
                var count = ScreenshotStore.RemoveViewer(pc);
                if (count == 0)
                    await AdminBroadcaster.Instance?.SendCommandToClient(pc, "STOP_SCREENSHOT_STREAM")!;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($"{{\"viewers\":{count}}}");
                return;
            }

            await next(ctx);
        }

        private static bool IsAuthorized(HttpContext ctx)
        {
            if (AdminAuth.IsAuthorized(ctx)) return true;
            var opToken = ctx.Request.Cookies["bib_op"];
            return opToken != null && OperatorApi.ValidateToken(opToken, out _);
        }
    }
}
