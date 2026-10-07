using System;
using System.Collections.Generic;
using System.Linq;

namespace BibAdminWeb
{
    /// <summary>
    /// Подсчёт посещений за период. Одно посещение — это:
    ///   • сессия за ПК вместе с услугами, оформленными во время неё
    ///     (на этот ПК или на тот же читательский билет);
    ///   • услуга (или чек из нескольких услуг) вне сессии;
    ///   • ручная отметка оператора «пришёл в зал».
    /// Для читателей с билетом соседние события склеиваются в одно посещение, если между
    /// ними прошло меньше VisitGapMinutes (Настройки → Посещения). Ручная отметка не
    /// добавляет посещение, если после неё в тот же день (до следующей отметки) читатель
    /// сел за ПК или взял услугу — тогда посещение уже посчитано по этому событию.
    /// Посетители без билета не склеиваются: каждое событие — отдельное посещение.
    /// </summary>
    public static class VisitCalc
    {
        /// <summary>Одно посещение для списка на вкладке оператора «Посещения».</summary>
        public sealed class VisitRow
        {
            public DateTime At { get; set; }               // когда пришёл (местное время)
            public long? MarkId { get; set; }              // id ручной отметки оператора (для удаления в админке)
            public string ReaderId { get; set; } = "";
            public string ReaderName { get; set; } = "";
            public bool HasMark { get; set; }              // оператор отметил вручную
            public bool HasSession { get; set; }           // сидел за ПК
            public bool HasService { get; set; }           // брал услугу
            public bool Active { get; set; }               // сессия ещё идёт
            public string PcNumber { get; set; } = "";
            public string OperatorName { get; set; } = "";
            public string Purpose { get; set; } = "";
            public string Comment { get; set; } = "";
        }

        public sealed class Result
        {
            public List<(string Id, Reader? Rd, bool IsReg, int Earned, List<ServiceTransaction> Svcs)> Visits = new();
            public List<VisitRow> Rows = new();    // те же посещения, по одному на строку, новые сверху
            public int ManualMarks;                // всего ручных отметок за период
            public int ManualOnlyVisits;           // из них посчитаны как отдельное посещение
            public Dictionary<string, int> Purposes = new();
        }

        private sealed class Unit
        {
            public string Id = "";
            public Reader? Rd;
            public bool IsReg;
            public DateTime Start, End;
            public int Earned;
            public List<ServiceTransaction> Svcs = new();
            public VisitRow Row = new();
        }

        private static DateTime Local(DateTime d) => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;

        private static bool SameId(string? a, string? b)
            => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Билет без единой цифры (пусто или один префикс «FAA») — посетитель без билета
        private static string NormId(string? readerId)
        {
            var id = readerId?.Trim() ?? "";
            return id.Any(char.IsDigit) ? id : "";
        }

        // false — билет удалённого читателя (не цифровой и его нет в базе): такое событие пропускаем.
        // Чисто цифровой номер — временный билет, считается как анонимный.
        private static bool Resolve(string readerId, Dictionary<string, Reader> readerMap, out Reader? rd, out bool isReg)
        {
            rd = null; isReg = false;
            if (readerId.Length == 0) return true;
            if (readerMap.TryGetValue(readerId, out var found)) { rd = found; isReg = true; return true; }
            return readerId.All(char.IsDigit);
        }

        private static string Name(Reader? rd, string? fallback)
        {
            if (rd != null && !string.IsNullOrWhiteSpace(rd.FullName)) return rd.FullName;
            return string.IsNullOrWhiteSpace(fallback) || fallback == "—" ? "" : fallback.Trim();
        }

        private static Dictionary<string, Reader> LoadReaderMap()
        {
            var readerMap = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
            foreach (var rd in ReaderStore.GetAll()) readerMap[rd.CardId] = rd;
            return readerMap;
        }

        /// <summary>
        /// Посещения для вкладки оператора: завершённые сессии, услуги, ручные отметки
        /// и сессии, которые идут прямо сейчас (в статистику они попадают после завершения).
        /// </summary>
        public static Result BuildLive(DateTime from, DateTime to)
        {
            var sessions = FinanceStore.Sessions.Where(s => s.EndTime >= from && s.EndTime < to).ToList();
            var allSvc = ServiceTransaction.All
                .Where(t => { var ts = t.CreatedAt.ToLocalTime(); return ts >= from && ts < to; }).ToList();

            var now = DateTime.Now;
            var active = new List<SessionRecord>();
            if (now >= from && now < to)
            {
                foreach (var c in AdminHub.KnownClients.Values.Where(c => c.IsSession))
                    active.Add(new SessionRecord
                    {
                        PcNumber = c.PcNumber,
                        ReaderId = c.ReaderId ?? "",
                        UserName = c.UserName ?? "",
                        OperatorName = c.StartedByOperatorName ?? "",
                        StartTime = Local(c.SessionStart ?? DateTime.UtcNow),
                        EndTime = now
                    });
            }
            sessions.AddRange(active);
            return Build(from, to, LoadReaderMap(), sessions, allSvc, new HashSet<SessionRecord>(active));
        }

        public static Result Build(DateTime from, DateTime to, Dictionary<string, Reader> readerMap,
            List<SessionRecord> sessions, List<ServiceTransaction> allSvc, HashSet<SessionRecord>? activeSessions = null)
        {
            var res = new Result();
            int gapMinutes = Math.Max(0, GlobalSettings.Load().VisitGapMinutes);

            var units = new List<Unit>();
            var sessionUnits = new List<Unit>();
            var usedSvcIds = new HashSet<string>();

            // 1. Сессии + услуги, оформленные на этот ПК во время сессии
            foreach (var s in sessions)
            {
                var start = Local(s.StartTime);
                var end = Local(s.EndTime);
                var linked = allSvc.Where(t =>
                    !string.IsNullOrEmpty(t.PcNumber) && t.PcNumber == s.PcNumber
                    && t.CreatedAt.ToLocalTime() >= start && t.CreatedAt.ToLocalTime() <= end).ToList();
                foreach (var t in linked) usedSvcIds.Add(t.Id);

                var id = NormId(s.ReaderId);
                if (!Resolve(id, readerMap, out var rd, out var isReg)) continue;
                var u = new Unit
                {
                    Id = id, Rd = rd, IsReg = isReg, Start = start, End = end, Earned = s.EarnedAmount, Svcs = linked,
                    Row = new VisitRow
                    {
                        At = start, ReaderId = id, ReaderName = Name(rd, s.UserName), HasSession = true,
                        HasService = linked.Count > 0, PcNumber = s.PcNumber, OperatorName = s.OperatorName ?? "",
                        Active = activeSessions != null && activeSessions.Contains(s)
                    }
                };
                units.Add(u);
                sessionUnits.Add(u);
            }

            // 2. Остальные услуги: если у этого же билета в этот момент шла сессия — относим к ней,
            //    иначе это отдельное событие (один чек = одно событие)
            foreach (var grp in allSvc
                .Where(t => !usedSvcIds.Contains(t.Id))
                .GroupBy(t => string.IsNullOrEmpty(t.BatchId) ? t.Id : "b:" + t.BatchId))
            {
                var items = grp.ToList();
                var first = items[0];
                var at = first.CreatedAt.ToLocalTime();
                var id = NormId(first.ReaderId);

                var host = sessionUnits.FirstOrDefault(u => SameId(u.Id, id) && at >= u.Start && at <= u.End);
                if (host != null) { host.Svcs.AddRange(items); host.Row.HasService = true; continue; }

                if (!Resolve(id, readerMap, out var rd, out var isReg)) continue;
                units.Add(new Unit
                {
                    Id = id, Rd = rd, IsReg = isReg, Start = at, End = at, Svcs = items,
                    Row = new VisitRow
                    {
                        At = at, ReaderId = id, ReaderName = Name(rd, first.ReaderName),
                        HasService = true, PcNumber = first.PcNumber ?? ""
                    }
                });
            }

            // 3. Читатели с билетом: события с перерывом меньше порога — одно посещение
            var merged = new List<Unit>();
            merged.AddRange(units.Where(u => !u.IsReg));
            foreach (var g in units.Where(u => u.IsReg).GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase))
            {
                Unit? cur = null;
                foreach (var u in g.OrderBy(x => x.Start))
                {
                    if (cur != null && (u.Start - cur.End).TotalMinutes < gapMinutes)
                    {
                        cur.Earned += u.Earned;
                        cur.Svcs.AddRange(u.Svcs);
                        if (u.End > cur.End) cur.End = u.End;
                        cur.Row.HasSession |= u.Row.HasSession;
                        cur.Row.HasService |= u.Row.HasService;
                        cur.Row.Active |= u.Row.Active;
                        if (u.Row.HasSession) cur.Row.PcNumber = u.Row.PcNumber;
                        if (cur.Row.OperatorName.Length == 0) cur.Row.OperatorName = u.Row.OperatorName;
                    }
                    else { cur = u; merged.Add(u); }
                }
            }

            foreach (var u in merged)
            {
                res.Visits.Add((u.Id, u.Rd, u.IsReg, u.Earned, u.Svcs));
                res.Rows.Add(u.Row);
            }

            // 4. Ручные отметки
            var marks = VisitStore.Snapshot()
                .Select(v => (V: v, At: v.CreatedAt.ToLocalTime()))
                .Where(m => m.At >= from && m.At < to)
                .OrderBy(m => m.At).ToList();
            res.ManualMarks = marks.Count;

            for (int i = 0; i < marks.Count; i++)
            {
                var (v, at) = marks[i];
                if (!string.IsNullOrWhiteSpace(v.Purpose))
                    res.Purposes[v.Purpose] = res.Purposes.GetValueOrDefault(v.Purpose) + 1;

                var id = NormId(v.ReaderId);
                if (!Resolve(id, readerMap, out var rd, out var isReg)) continue;

                if (id.Length > 0)
                {
                    // Отметка «действует» до следующей отметки этого же читателя в тот же день
                    var until = at.Date.AddDays(1);
                    for (int j = i + 1; j < marks.Count; j++)
                        if (SameId(NormId(marks[j].V.ReaderId), id) && marks[j].At.Date == at.Date) { until = marks[j].At; break; }

                    var host = merged.Where(u => SameId(u.Id, id)
                            && ((u.Start >= at && u.Start < until) || (u.Start <= at && u.End >= at)))
                        .OrderBy(u => u.Start).FirstOrDefault();
                    if (host != null)
                    {
                        // Посещение уже посчитано по сессии/услуге — отметка только дополняет строку
                        host.Row.HasMark = true;
                        host.Row.MarkId ??= v.Id;
                        if (at < host.Row.At) host.Row.At = at;
                        if (host.Row.Purpose.Length == 0) host.Row.Purpose = v.Purpose ?? "";
                        if (host.Row.Comment.Length == 0) host.Row.Comment = v.Comment ?? "";
                        if (host.Row.OperatorName.Length == 0) host.Row.OperatorName = v.OperatorName ?? "";
                        continue;
                    }
                }

                res.Visits.Add((id, rd, isReg, 0, new List<ServiceTransaction>()));
                res.Rows.Add(new VisitRow
                {
                    At = at, ReaderId = id, ReaderName = Name(rd, v.ReaderName), HasMark = true, MarkId = v.Id,
                    OperatorName = v.OperatorName ?? "", Purpose = v.Purpose ?? "", Comment = v.Comment ?? ""
                });
                res.ManualOnlyVisits++;
            }

            res.Rows = res.Rows.OrderByDescending(r => r.At).ToList();
            return res;
        }
    }
}
