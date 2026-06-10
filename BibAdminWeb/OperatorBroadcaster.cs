using System.Linq;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class OperatorBroadcaster
    {
        public static OperatorBroadcaster? Instance { get; private set; }
        private readonly IHubContext<OperatorHub> _ctx;

        public OperatorBroadcaster(IHubContext<OperatorHub> ctx)
        {
            _ctx = ctx;
            Instance = this;
            AdminHub.ClientUpdated += cs => _ = _ctx.Clients.All.SendAsync("pcUpdated", ClientDto(cs));
            AdminHub.ClientsChanged += () =>
            {
                var all = AdminHub.KnownClients.Values.Select(ClientDto).ToList();
                _ = _ctx.Clients.All.SendAsync("allPcsUpdated", all);
            };
            AdminHub.ClientOfflineWithSession += cs => _ = _ctx.Clients.All.SendAsync("offlineAlert", new
            {
                pcNumber = cs.PcNumber,
                sessionType = cs.SessionType,
                elapsed = cs.ElapsedAtDisconnect
            });
        }

        public void NotifyOfflineResolved(string pcNumber, string decision)
            => _ = _ctx.Clients.All.SendAsync("offlineResolved", new { pcNumber, decision });

        public void NotifySessionEndedByStaff(string pcNumber, string userName, int durationSeconds, int earned)
            => _ = _ctx.Clients.All.SendAsync("sessionEndedByStaff",
                new { pcNumber, userName, durationSeconds, earned });

        public Task NotifyServerRestartingAsync(string reason)
            => _ctx.Clients.All.SendAsync("serverRestarting", new { reason });

        /// <summary>
        /// Уведомляет всех подключённых операторов об изменении прав.
        /// Каждый клиент сам решает, относится ли сообщение к нему (по operatorId).
        /// </summary>
        public Task NotifyPermissionsUpdatedAsync(string operatorId)
            => _ctx.Clients.All.SendAsync("permissionsUpdated", new { operatorId });

        public void PushServiceTypes()
        {
            var settings = GlobalSettings.Load();
            var services = settings.Services.Where(s => s.IsActive)
                .Select(s => new { id = s.Id, name = s.Name, unit = s.Unit, price = s.Price }).ToList();
            _ = _ctx.Clients.All.SendAsync("serviceTypes", services);
        }

        public void PushSessionFields()
        {
            var settings = GlobalSettings.Load();
            _ = _ctx.Clients.All.SendAsync("sessionFields", new
            {
                requireReaderId = settings.RequireReaderId,
                requireUserName = settings.RequireUserName,
                workdayEnd = settings.WorkdayEnd
            });
        }

        public static object ClientDto(ClientState cs) => new
        {
            pcNumber = cs.PcNumber, pcNumberValue = cs.PcNumberValue, status = cs.Status,
            sessionType = cs.SessionType, isOnline = cs.IsOnline, isSession = cs.IsSession,
            isPaused = cs.IsPaused, isLocked = cs.IsLocked, isFree = cs.IsFree,
            // Для offline+Continue считаем elapsed с учётом времени с момента обрыва
            elapsedSeconds = (!cs.IsOnline && cs.IsSession && !cs.IsPaused && cs.DisconnectedAt.HasValue)
                ? cs.ElapsedAtDisconnect + (int)(System.DateTime.UtcNow - cs.DisconnectedAt.Value).TotalSeconds
                : cs.ElapsedSeconds,
            limitSeconds = cs.LimitSeconds, paidAmount = cs.PaidAmount,
            accumulatedSeconds = cs.AccumulatedSeconds, sessionStart = cs.SessionStart?.ToString("o"),
            userName = cs.UserName, readerId = cs.ReaderId, ip = cs.Ip,
            disconnectedAt = cs.DisconnectedAt?.ToString("o"), elapsedAtDisconnect = cs.ElapsedAtDisconnect
        };
    }
}
