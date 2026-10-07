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
        public sealed class Result
        {
            public List<(string Id, Reader? Rd, bool IsReg, int Earned, List<ServiceTransaction> Svcs)> Visits = new();
            public int ManualMarks;        // всего ручных отметок за период
            public int ManualOnlyVisits;   // из них посчитаны как отдельное посещение
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
        }

        private static DateTime Local(DateTime d) => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;

        private static bool SameId(string? a, string? b)
            => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // false — билет удалённого читателя (не цифровой и его нет в базе): такое событие пропускаем.
        // Чисто цифровой номер — временный билет, считается как анонимный.
        private static bool Resolve(string? readerId, Dictionary<string, Reader> readerMap, out Reader? rd, out bool isReg)
        {
            rd = null; isReg = false;
            if (string.IsNullOrEmpty(readerId)) return true;
            if (readerMap.TryGetValue(readerId, out var found)) { rd = found; isReg = true; return true; }
            return readerId.All(char.IsDigit);
        }

        /// <summary>Число посещений за период по тем же правилам, что и в статистике.</summary>
        public static int CountVisits(DateTime from, DateTime to)
        {
            var readerMap = new Dictionary<string, Reader>(StringComparer.OrdinalIgnoreCase);
            foreach (var rd in ReaderStore.GetAll()) readerMap[rd.CardId] = rd;
            var sessions = FinanceStore.Sessions.Where(s => s.EndTime >= from && s.EndTime < to).ToList();
            var allSvc = ServiceTransaction.All
                .Where(t => { var ts = t.CreatedAt.ToLocalTime(); return ts >= from && ts < to; }).ToList();
            return Build(from, to, readerMap, sessions, allSvc).Visits.Count;
        }

        public static Result Build(DateTime from, DateTime to, Dictionary<string, Reader> readerMap,
            List<SessionRecord> sessions, List<ServiceTransaction> allSvc)
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

                if (!Resolve(s.ReaderId, readerMap, out var rd, out var isReg)) continue;
                var u = new Unit { Id = s.ReaderId ?? "", Rd = rd, IsReg = isReg, Start = start, End = end, Earned = s.EarnedAmount, Svcs = linked };
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

                var host = sessionUnits.FirstOrDefault(u => SameId(u.Id, first.ReaderId) && at >= u.Start && at <= u.End);
                if (host != null) { host.Svcs.AddRange(items); continue; }

                if (!Resolve(first.ReaderId, readerMap, out var rd, out var isReg)) continue;
                units.Add(new Unit { Id = first.ReaderId ?? "", Rd = rd, IsReg = isReg, Start = at, End = at, Svcs = items });
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
                    }
                    else { cur = u; merged.Add(u); }
                }
            }

            foreach (var u in merged) res.Visits.Add((u.Id, u.Rd, u.IsReg, u.Earned, u.Svcs));

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

                var id = v.ReaderId ?? "";
                if (!Resolve(id, readerMap, out var rd, out var isReg)) continue;

                if (id.Length > 0)
                {
                    // Отметка «действует» до следующей отметки этого же читателя в тот же день
                    var until = at.Date.AddDays(1);
                    for (int j = i + 1; j < marks.Count; j++)
                        if (SameId(marks[j].V.ReaderId, id) && marks[j].At.Date == at.Date) { until = marks[j].At; break; }

                    bool absorbed = merged.Any(u => SameId(u.Id, id)
                        && ((u.Start >= at && u.Start < until) || (u.Start <= at && u.End >= at)));
                    if (absorbed) continue;
                }

                res.Visits.Add((id, rd, isReg, 0, new List<ServiceTransaction>()));
                res.ManualOnlyVisits++;
            }

            return res;
        }
    }
}
