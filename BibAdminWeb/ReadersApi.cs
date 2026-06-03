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
                    updatedAt    = reader.UpdatedAt,
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

            // ─── Quick-add reader by card ID (admin) ──────────────────────────
            if (path == "/api/admin/readers/quick-add" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                string body = await new System.IO.StreamReader(ctx.Request.Body).ReadToEndAsync();
                string cardId = "";
                try { using var doc = System.Text.Json.JsonDocument.Parse(body); cardId = doc.RootElement.GetProperty("cardId").GetString()?.Trim() ?? ""; } catch { }
                if (string.IsNullOrWhiteSpace(cardId)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"Не указан cardId\"}"); return; }
                ReaderStore.QuickAdd(cardId);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            // ─── Admin: update reader ──────────────────────────────────────────
            if (path == "/api/admin/readers" && method == "PUT")
            {
                ctx.Response.ContentType = "application/json";
                try
                {
                    var reader = await JsonSerializer.DeserializeAsync<Reader>(ctx.Request.Body, _json);
                    if (reader == null || string.IsNullOrWhiteSpace(reader.CardId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsync("{\"error\":\"Неверные данные\"}");
                        return;
                    }
                    ReaderStore.Update(reader);
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, _json));
                }
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

            // ─── Admin: period report ──────────────────────────────────────────
            if (path == "/api/admin/readers/report" && method == "GET")
            {
                var period  = ctx.Request.Query["period"].ToString();
                var dateStr = ctx.Request.Query["date"].ToString();
                ctx.Response.ContentType = "application/json";
                try
                {
                    var result = BuildReport(period, dateStr);
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(result, _json));
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, _json));
                }
                return;
            }

            // ─── Admin: period report export ────────────────────────────────
            if (path == "/api/admin/readers/report/export" && method == "GET")
            {
                var period  = ctx.Request.Query["period"].ToString();
                var dateStr = ctx.Request.Query["date"].ToString();
                try
                {
                    var result = BuildReport(period, dateStr);
                    var bytes  = BuildReportExcel(result, period, dateStr);
                    ctx.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    ctx.Response.Headers["Content-Disposition"] =
                        $"attachment; filename=report_{dateStr.Replace("-", "")}.xlsx";
                    await ctx.Response.Body.WriteAsync(bytes);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка экспорта отчёта: {ex.Message}");
                    ctx.Response.StatusCode = 400;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, _json));
                }
                return;
            }

            await next(ctx);
        }

        // ── Report DTOs ──────────────────────────────────────────────────────
        private sealed class ServiceQtyDto
        {
            public int Qty    { get; set; }
            public int Amount { get; set; }
        }

        private sealed class VisitRowDto
        {
            public DateTime Timestamp      { get; set; }
            public string   ReaderId       { get; set; } = "";
            public string   ReaderName     { get; set; } = "";
            public string   ReaderCategory { get; set; } = "";
            public string   ReaderStatus   { get; set; } = "";
            public string   PcNumber       { get; set; } = "";
            public bool     HasSession     { get; set; }
            public int      DurationMin    { get; set; }
            public int      SessionAmount  { get; set; }
            public Dictionary<string, ServiceQtyDto> Services { get; set; } = new();
            public int      TotalAmount    { get; set; }
        }

        private static (string name, string category, string status) ResolveReader(
            string readerId, string fallbackName, Dictionary<string, Reader> map)
        {
            if (string.IsNullOrEmpty(readerId))
                return ("—", "", "anonymous");
            if (map.TryGetValue(readerId, out var r))
                return (r.FullName, r.Category, "registered");
            // Purely numeric ID → temporary card (no name in DB by design)
            if (readerId.All(char.IsDigit))
                return ($"Временный №{readerId}", "Временный", "temp");
            var name = !string.IsNullOrEmpty(fallbackName) && fallbackName != "—"
                ? fallbackName : $"Незарег. {readerId}";
            return (name, "", "unregistered");
        }

        private static (DateTime from, DateTime to) ParsePeriod(string period, string dateStr)
        {
            if (period == "day")
            {
                var f = DateTime.ParseExact(dateStr, "yyyy-MM-dd", null).Date;
                return (f, f.AddDays(1));
            }
            if (period == "month")
            {
                var p = dateStr.Split('-');
                var f = new DateTime(int.Parse(p[0]), int.Parse(p[1]), 1);
                return (f, f.AddMonths(1));
            }
            throw new ArgumentException($"Неверный период: {period}");
        }

        private static object BuildReport(string period, string dateStr)
        {
            var (from, to) = ParsePeriod(period, dateStr);

            var readerMap = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ReaderStore.GetAll()) readerMap[r.CardId] = r;

            // Filter sources
            var sessions = FinanceStore.Sessions
                .Where(s => s.EndTime >= from && s.EndTime < to).ToList();
            var services = ServiceTransaction.All
                .Where(t => { var ts = t.CreatedAt.ToLocalTime(); return ts >= from && ts < to; }).ToList();

            // Dynamic service columns — all service types that appear in period
            var serviceColumns = services
                .GroupBy(t => t.ServiceTypeId)
                .Select(g => new { id = g.Key, name = g.First().ServiceName })
                .OrderBy(c => c.name).ToList();

            var usedServiceIds = new HashSet<string>();
            var rows = new List<VisitRowDto>();

            // ── Session rows — find services that happened during each session on the same PC ──
            foreach (var s in sessions)
            {
                var startLocal = s.StartTime.Kind == DateTimeKind.Utc
                    ? s.StartTime.ToLocalTime() : s.StartTime;
                var endLocal = s.EndTime;

                var linked = services
                    .Where(t => !string.IsNullOrEmpty(t.PcNumber)
                             && t.PcNumber == s.PcNumber
                             && t.CreatedAt.ToLocalTime() >= startLocal
                             && t.CreatedAt.ToLocalTime() <= endLocal)
                    .ToList();
                foreach (var t in linked) usedServiceIds.Add(t.Id);

                var svcDict = linked
                    .GroupBy(t => t.ServiceTypeId)
                    .ToDictionary(g => g.Key,
                        g => new ServiceQtyDto { Qty = g.Sum(t => t.Quantity), Amount = g.Sum(t => t.TotalAmount) });

                var (name, cat, status) = ResolveReader(s.ReaderId, s.UserName, readerMap);
                rows.Add(new VisitRowDto
                {
                    Timestamp      = startLocal,
                    ReaderId       = s.ReaderId,
                    ReaderName     = name, ReaderCategory = cat, ReaderStatus = status,
                    PcNumber       = s.PcNumber,
                    HasSession     = true,
                    DurationMin    = s.DurationSeconds / 60,
                    SessionAmount  = s.EarnedAmount,
                    Services       = svcDict,
                    TotalAmount    = s.EarnedAmount + svcDict.Values.Sum(v => v.Amount)
                });
            }

            // ── Standalone services — group by BatchId (or individually if no batch) ──
            var standalone = services.Where(t => !usedServiceIds.Contains(t.Id)).ToList();
            foreach (var grp in standalone.GroupBy(t => string.IsNullOrEmpty(t.BatchId) ? t.Id : ("b:" + t.BatchId)))
            {
                var items = grp.ToList();
                var first = items[0];
                var (name, cat, status) = ResolveReader(first.ReaderId, first.ReaderName, readerMap);
                var svcDict = items
                    .GroupBy(t => t.ServiceTypeId)
                    .ToDictionary(g => g.Key,
                        g => new ServiceQtyDto { Qty = g.Sum(t => t.Quantity), Amount = g.Sum(t => t.TotalAmount) });
                rows.Add(new VisitRowDto
                {
                    Timestamp      = first.CreatedAt.ToLocalTime(),
                    ReaderId       = first.ReaderId,
                    ReaderName     = name, ReaderCategory = cat, ReaderStatus = status,
                    PcNumber       = first.PcNumber ?? "",
                    HasSession     = false,
                    Services       = svcDict,
                    TotalAmount    = items.Sum(t => t.TotalAmount)
                });
            }

            rows.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));

            var uniqueReaders = rows
                .Where(r => !string.IsNullOrEmpty(r.ReaderId))
                .Select(r => r.ReaderId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var svcSummary = serviceColumns.ToDictionary(
                c => c.id,
                c => new {
                    name   = c.name,
                    qty    = rows.Sum(r => r.Services.TryGetValue(c.id, out var v) ? v.Qty    : 0),
                    amount = rows.Sum(r => r.Services.TryGetValue(c.id, out var v) ? v.Amount : 0)
                });

            return new
            {
                serviceColumns,
                items = rows.Select(r => new {
                    timestamp      = r.Timestamp,
                    readerId       = r.ReaderId,
                    readerName     = r.ReaderName,
                    readerCategory = r.ReaderCategory,
                    readerStatus   = r.ReaderStatus,
                    pcNumber       = r.PcNumber,
                    hasSession     = r.HasSession,
                    durationMin    = r.DurationMin,
                    sessionAmount  = r.SessionAmount,
                    services       = r.Services.ToDictionary(
                        k => k.Key,
                        v => new { qty = v.Value.Qty, amount = v.Value.Amount }),
                    totalAmount    = r.TotalAmount
                }),
                summary = new {
                    totalVisits        = rows.Count,
                    totalSessions      = rows.Count(r => r.HasSession),
                    totalUniqueReaders = uniqueReaders,
                    totalDurationMin   = rows.Sum(r => r.DurationMin),
                    totalAmount        = rows.Sum(r => r.TotalAmount),
                    servicesSummary    = svcSummary
                }
            };
        }

        private static byte[] BuildReportExcel(object reportObj, string period, string dateStr)
        {
            // Re-use BuildReport logic to get structured visit rows
            var (from, to) = ParsePeriod(period, dateStr);

            var readerMap = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ReaderStore.GetAll()) readerMap[r.CardId] = r;

            var sessions = FinanceStore.Sessions
                .Where(s => s.EndTime >= from && s.EndTime < to).ToList();
            var services = ServiceTransaction.All
                .Where(t => { var ts = t.CreatedAt.ToLocalTime(); return ts >= from && ts < to; }).ToList();

            var svcCols = services
                .GroupBy(t => t.ServiceTypeId)
                .Select(g => new { id = g.Key, name = g.First().ServiceName })
                .OrderBy(c => c.name).ToList();

            var usedIds = new HashSet<string>();
            var rows = new List<VisitRowDto>();

            foreach (var s in sessions)
            {
                var sl = s.StartTime.Kind == DateTimeKind.Utc ? s.StartTime.ToLocalTime() : s.StartTime;
                var el = s.EndTime;
                var linked = services.Where(t => !string.IsNullOrEmpty(t.PcNumber)
                    && t.PcNumber == s.PcNumber
                    && t.CreatedAt.ToLocalTime() >= sl && t.CreatedAt.ToLocalTime() <= el).ToList();
                foreach (var t in linked) usedIds.Add(t.Id);
                var svcDict = linked.GroupBy(t => t.ServiceTypeId)
                    .ToDictionary(g => g.Key,
                        g => new ServiceQtyDto { Qty = g.Sum(t => t.Quantity), Amount = g.Sum(t => t.TotalAmount) });
                var (nm, cat, _) = ResolveReader(s.ReaderId, s.UserName, readerMap);
                rows.Add(new VisitRowDto
                {
                    Timestamp = sl, ReaderId = s.ReaderId, ReaderName = nm, ReaderCategory = cat,
                    PcNumber = s.PcNumber, HasSession = true, DurationMin = s.DurationSeconds / 60,
                    SessionAmount = s.EarnedAmount, Services = svcDict,
                    TotalAmount = s.EarnedAmount + svcDict.Values.Sum(v => v.Amount)
                });
            }
            foreach (var grp in services.Where(t => !usedIds.Contains(t.Id))
                .GroupBy(t => string.IsNullOrEmpty(t.BatchId) ? t.Id : ("b:" + t.BatchId)))
            {
                var items = grp.ToList(); var first = items[0];
                var (nm, cat, _) = ResolveReader(first.ReaderId, first.ReaderName, readerMap);
                var svcDict = items.GroupBy(t => t.ServiceTypeId)
                    .ToDictionary(g => g.Key,
                        g => new ServiceQtyDto { Qty = g.Sum(t => t.Quantity), Amount = g.Sum(t => t.TotalAmount) });
                rows.Add(new VisitRowDto
                {
                    Timestamp = first.CreatedAt.ToLocalTime(), ReaderId = first.ReaderId,
                    ReaderName = nm, ReaderCategory = cat, PcNumber = first.PcNumber ?? "",
                    HasSession = false, Services = svcDict, TotalAmount = items.Sum(t => t.TotalAmount)
                });
            }
            rows.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            var periodLabel = period == "day"
                ? from.ToString("dd.MM.yyyy")
                : from.ToString("MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));

            // Fixed columns: DateTime | Reader | Category | PC | Duration
            // + dynamic service columns + Total
            int fixedCols = 5;
            int totalCols = fixedCols + svcCols.Count + 1;

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Отчёт");
            ws.Column(1).Width = 16; ws.Column(2).Width = 30;
            ws.Column(3).Width = 18; ws.Column(4).Width = 9; ws.Column(5).Width = 8;
            for (int i = 0; i < svcCols.Count; i++) ws.Column(fixedCols + 1 + i).Width = 14;
            ws.Column(totalCols).Width = 13;

            WriteHeader(ws, 1, $"Отчёт за {periodLabel}");
            var hdr = ws.Row(2);
            hdr.Cell(1).Value = "Дата/Время";
            hdr.Cell(2).Value = "Читатель";
            hdr.Cell(3).Value = "Категория";
            hdr.Cell(4).Value = "ПК";
            hdr.Cell(5).Value = "Мин";
            for (int i = 0; i < svcCols.Count; i++)
                hdr.Cell(fixedCols + 1 + i).Value = svcCols[i].name;
            hdr.Cell(totalCols).Value = "Итого (сум)";
            StyleHeader(ws.Row(2), totalCols);

            int row = 3;
            foreach (var r in rows)
            {
                ws.Cell(row, 1).Value = r.Timestamp.ToString("dd.MM.yyyy HH:mm");
                ws.Cell(row, 2).Value = r.ReaderName;
                ws.Cell(row, 3).Value = r.ReaderCategory;
                ws.Cell(row, 4).Value = r.PcNumber;
                ws.Cell(row, 5).Value = r.DurationMin > 0 ? (int?)r.DurationMin : null;
                for (int i = 0; i < svcCols.Count; i++)
                {
                    if (r.Services.TryGetValue(svcCols[i].id, out var sv))
                        ws.Cell(row, fixedCols + 1 + i).Value = sv.Qty;
                }
                ws.Cell(row, totalCols).Value = r.TotalAmount;
                row++;
            }

            row++;
            ws.Cell(row, 1).Value = "Итого:";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 5).Value = rows.Sum(r => r.DurationMin);
            for (int i = 0; i < svcCols.Count; i++)
            {
                var cid = svcCols[i].id;
                ws.Cell(row, fixedCols + 1 + i).Value =
                    rows.Sum(r => r.Services.TryGetValue(cid, out var v) ? v.Qty : 0);
            }
            ws.Cell(row, totalCols).Value = rows.Sum(r => r.TotalAmount);
            ws.Cell(row, totalCols).Style.Font.Bold = true;

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        // ── Excel parsing ────────────────────────────────────────────────────
        private static List<Reader> ParseExcel(Stream stream)
        {
            var result = new List<Reader>();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheet(1);

            // Find column indices from header row
            var colCardId    = -1;
            var colName      = -1;
            var colBirth     = -1;
            var colCategory  = -1;
            var colRegDate   = -1;
            var colUpdatedAt = -1;
            var colGender    = -1;

            var headerRow = ws.Row(1);
            foreach (var cell in headerRow.CellsUsed())
            {
                var hdr = cell.GetString().Trim().ToLower();
                var col = cell.Address.ColumnNumber;
                if (hdr.Contains("id пользователя") || hdr == "id")          colCardId    = col;
                else if (hdr.Contains("имя пользователя"))                    colName      = col;
                else if (hdr.Contains("дата рождения"))                       colBirth     = col;
                else if (hdr.Contains("категория"))                           colCategory  = col;
                else if (hdr.Contains("дата последнего обновления"))          colUpdatedAt = col;
                else if (hdr.Contains("дата регистрации"))                    colRegDate   = col;
                else if (hdr.Contains("пол"))                                 colGender    = col;
            }

            if (colCardId < 0 || colName < 0)
                throw new InvalidOperationException("Не найдены обязательные столбцы «ID пользователя» и «Имя пользователя»");

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                var cardId = ws.Cell(row, colCardId).GetString().Trim();
                if (string.IsNullOrWhiteSpace(cardId)) continue;

                var registeredAt = colRegDate  > 0 ? NormalizeDate(ws.Cell(row, colRegDate))  : "";
                var updatedAt    = colUpdatedAt > 0 ? NormalizeDate(ws.Cell(row, colUpdatedAt)) : "";
                // -1 or unparseable means no renewal — treat as registration date
                if (string.IsNullOrEmpty(updatedAt)) updatedAt = registeredAt;

                var reader = new Reader
                {
                    CardId       = cardId,
                    FullName     = colName     > 0 ? ws.Cell(row, colName).GetString().Trim()     : "",
                    BirthDate    = colBirth    > 0 ? NormalizeDate(ws.Cell(row, colBirth))         : "",
                    Category     = colCategory > 0 ? ws.Cell(row, colCategory).GetString().Trim() : "",
                    RegisteredAt = registeredAt,
                    UpdatedAt    = updatedAt,
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
                    // -1 or any non-positive number means "no date" (e.g. not renewed)
                    if (cell.DataType == XLDataType.Number && cell.GetDouble() <= 0)
                        return "";
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

        private static void StyleHeader(IXLRow row, int cols = 6)
        {
            foreach (var cell in row.Cells(1, cols))
            {
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
                cell.Style.Font.FontColor = XLColor.White;
            }
        }
    }
}
