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

        public void PushServiceTypes()
        {
            var settings = GlobalSettings.Load();
            var services = settings.Services.Where(s => s.IsActive)
                .Select(s => new { id = s.Id, name = s.Name, unit = s.Unit, price = s.Price }).ToList();
            _ = _ctx.Clients.All.SendAsync("serviceTypes", services);
        }

        public static object ClientDto(ClientState cs) => new
        {
            pcNumber = cs.PcNumber, pcNumberValue = cs.PcNumberValue, status = cs.Status,
            sessionType = cs.SessionType, isOnline = cs.IsOnline, isSession = cs.IsSession,
            isPaused = cs.IsPaused, isLocked = cs.IsLocked, isFree = cs.IsFree,
            elapsedSeconds = cs.ElapsedSeconds, limitSeconds = cs.LimitSeconds, paidAmount = cs.PaidAmount,
            accumulatedSeconds = cs.AccumulatedSeconds, sessionStart = cs.SessionStart?.ToString("o"),
            userName = cs.UserName, readerId = cs.ReaderId, ip = cs.Ip,
            disconnectedAt = cs.DisconnectedAt?.ToString("o"), elapsedAtDisconnect = cs.ElapsedAtDisconnect
        };
    }
}
