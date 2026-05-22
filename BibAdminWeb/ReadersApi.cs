using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    public static class ReadersApi
    {
        private static readonly JsonSerializerOptions _json =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static async Task Handle(HttpContext ctx, RequestDelegate next)
        {
            var path   = ctx.Request.Path.Value ?? "";
            var method = ctx.Request.Method;

            // ─── Operator lookup (no admin auth needed) ─────────────────────
            if (path.StartsWith("/api/readers/lookup/") && method == "GET")
            {
                var cardId = Uri.UnescapeDataString(path["/api/readers/lookup/".Length..]);
                var reader = ReaderStore.GetByCardId(cardId);
                ctx.Response.ContentType = "application/json";
                if (reader == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("{}");
                    return;
                }
                var age = ReaderStore.CalcAge(reader.BirthDate);
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    cardId       = reader.CardId,
                    fullName     = reader.FullName,
                    category     = reader.Category,
                    gender       = reader.Gender,
                    birthDate    = reader.BirthDate,
                    registeredAt = reader.RegisteredAt,
                    age          = age >= 0 ? age : (int?)null,
                }, _json));
                return;
            }

            // ─── Admin: list readers ────────────────────────────────────────
            // Note: admin auth already enforced by AdminApi middleware for /api/admin/* paths
            if (path == "/api/admin/readers" && method == "GET")
            {
                var search = ctx.Request.Query["search"].ToString();
                var list = ReaderStore.GetAll(string.IsNullOrWhiteSpace(search) ? null : search);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(list, _json));
                return;
            }

            // ─── Admin: import Excel ────────────────────────────────────────
            if (path == "/api/admin/readers/import" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                try
                {
                    if (!ctx.Request.HasFormContentType)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("{\"error\":\"Ожидается multipart/form-data\"}");
                        return;
                    }
                    var form = await ctx.Request.ReadFormAsync();
                    var file = form.Files.GetFile("file");
                    if (file == null || file.Length == 0)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("{\"error\":\"Файл не найден\"}");
                        return;
                    }

                    var readers = ParseExcel(file.OpenReadStream());
                    var result  = ReaderStore.Import(readers);
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(result, _json));
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка импорта Excel: {ex.Message}");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                }
                return;
            }

            // ─── Admin: export stats Excel ──────────────────────────────────
            if (path == "/api/admin/readers/stats/export" && method == "GET")
            {
                try
                {
                    var bytes = BuildStatsExcel();
                    ctx.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    ctx.Response.Headers["Content-Disposition"] =
                        $"attachment; filename=readers_stats_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                    await ctx.Response.Body.WriteAsync(bytes);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка экспорта статистики: {ex.Message}");
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                }
                return;
            }

            await next(ctx);
        }

        // ── Excel parsing ────────────────────────────────────────────────────
        private static List<Reader> ParseExcel(Stream stream)
        {
            var result = new List<Reader>();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheet(1);

            // Find column indices from header row
            var colCardId   = -1;
            var colName     = -1;
            var colBirth    = -1;
            var colCategory = -1;
            var colRegDate  = -1;
            var colGender   = -1;

            var headerRow = ws.Row(1);
            foreach (var cell in headerRow.CellsUsed())
            {
                var hdr = cell.GetString().Trim().ToLower();
                var col = cell.Address.ColumnNumber;
                if (hdr.Contains("id пользователя") || hdr == "id")          colCardId   = col;
                else if (hdr.Contains("имя пользователя"))                    colName     = col;
                else if (hdr.Contains("дата рождения"))                       colBirth    = col;
                else if (hdr.Contains("категория"))                           colCategory = col;
                else if (hdr.Contains("дата регистрации"))                    colRegDate  = col;
                else if (hdr.Contains("пол"))                                 colGender   = col;
            }

            if (colCardId < 0 || colName < 0)
                throw new InvalidOperationException("Не найдены обязательные столбцы «ID пользователя» и «Имя пользователя»");

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                var cardId = ws.Cell(row, colCardId).GetString().Trim();
                if (string.IsNullOrWhiteSpace(cardId)) continue;

                var reader = new Reader
                {
                    CardId       = cardId,
                    FullName     = colName     > 0 ? ws.Cell(row, colName).GetString().Trim()     : "",
                    BirthDate    = colBirth    > 0 ? NormalizeDate(ws.Cell(row, colBirth))         : "",
                    Category     = colCategory > 0 ? ws.Cell(row, colCategory).GetString().Trim() : "",
                    RegisteredAt = colRegDate  > 0 ? NormalizeDate(ws.Cell(row, colRegDate))       : "",
                    Gender       = colGender   > 0 ? NormalizeGender(ws.Cell(row, colGender).GetString().Trim()) : "",
                };
                result.Add(reader);
            }
            return result;
        }

        private static string NormalizeDate(IXLCell cell)
        {
            // Try as actual Excel date first
            if (cell.DataType == XLDataType.DateTime || cell.DataType == XLDataType.Number)
            {
                try
                {
                    var dt = cell.GetDateTime();
                    return dt.ToString("dd-MM-yyyy");
                }
                catch { }
            }

            var s = cell.GetString().Trim();
            if (string.IsNullOrEmpty(s)) return "";

            // Try common formats: "21-09-2007", "21.09.2007", "2007-09-21"
            string[] formats = { "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "d-M-yyyy", "d.M.yyyy" };
            if (DateTime.TryParseExact(s, formats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt2))
                return dt2.ToString("dd-MM-yyyy");

            return s; // return as-is if unparseable
        }

        private static string NormalizeGender(string raw)
        {
            var s = raw.ToLower().Trim();
            if (s is "женщины" or "женский" or "ж" or "f" or "female") return "Ж";
            if (s is "мужчины" or "мужской" or "м" or "m" or "male")   return "М";
            return "";
        }

        // ── Excel stats export ───────────────────────────────────────────────
        private static byte[] BuildStatsExcel()
        {
            var (total, byGender, byCategory, byAge) = ReaderStore.GetStats();
            var allReaders = ReaderStore.GetAll();

            using var wb = new XLWorkbook();

            // ── Sheet 1: Summary ────────────────────────────────────────────
            var ws1 = wb.Worksheets.Add("Сводная статистика");
            ws1.Column(1).Width = 28;
            ws1.Column(2).Width = 14;

            int r = 1;
            WriteHeader(ws1, r++, $"Статистика читателей — {DateTime.Now:dd.MM.yyyy}");
            ws1.Cell(r++, 1).Value = $"Всего в базе: {total}";

            r++;
            WriteSubHeader(ws1, r++, "По полу");
            foreach (var kv in byGender)
            {
                ws1.Cell(r, 1).Value = kv.Key;
                ws1.Cell(r, 2).Value = kv.Value;
                r++;
            }

            r++;
            WriteSubHeader(ws1, r++, "По возрастным группам");
            foreach (var kv in byAge)
            {
                ws1.Cell(r, 1).Value = kv.Key;
                ws1.Cell(r, 2).Value = kv.Value;
                r++;
            }

            r++;
            WriteSubHeader(ws1, r++, "По категориям");
            foreach (var kv in byCategory)
            {
                ws1.Cell(r, 1).Value = kv.Key;
                ws1.Cell(r, 2).Value = kv.Value;
                r++;
            }

            // ── Sheet 2: Full reader list ───────────────────────────────────
            var ws2 = wb.Worksheets.Add("Список читателей");
            ws2.Column(1).Width = 18;
            ws2.Column(2).Width = 35;
            ws2.Column(3).Width = 14;
            ws2.Column(4).Width = 8;
            ws2.Column(5).Width = 22;
            ws2.Column(6).Width = 14;

            var hdr = ws2.Row(1);
            hdr.Cell(1).Value = "ID билета";
            hdr.Cell(2).Value = "ФИО";
            hdr.Cell(3).Value = "Дата рождения";
            hdr.Cell(4).Value = "Пол";
            hdr.Cell(5).Value = "Категория";
            hdr.Cell(6).Value = "Дата регистрации";
            StyleHeader(ws2.Row(1));

            int row = 2;
            foreach (var rd in allReaders)
            {
                ws2.Cell(row, 1).Value = rd.CardId;
                ws2.Cell(row, 2).Value = rd.FullName;
                ws2.Cell(row, 3).Value = rd.BirthDate;
                ws2.Cell(row, 4).Value = rd.Gender;
                ws2.Cell(row, 5).Value = rd.Category;
                ws2.Cell(row, 6).Value = rd.RegisteredAt;
                row++;
            }

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private static void WriteHeader(IXLWorksheet ws, int row, string text)
        {
            var cell = ws.Cell(row, 1);
            cell.Value = text;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 13;
        }

        private static void WriteSubHeader(IXLWorksheet ws, int row, string text)
        {
            var cell = ws.Cell(row, 1);
            cell.Value = text;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        private static void StyleHeader(IXLRow row)
        {
            foreach (var cell in row.Cells(1, 6))
            {
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
                cell.Style.Font.FontColor = XLColor.White;
            }
        }
    }
}
