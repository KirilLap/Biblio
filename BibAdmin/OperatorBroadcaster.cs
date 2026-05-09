using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace BibAdmin
{
    // Singleton: подписывается на события AdminHub и рассылает обновления всем веб-операторам.
    public class OperatorBroadcaster
    {
        public static OperatorBroadcaster? Instance { get; private set; }
        private readonly IHubContext<OperatorHub> _ctx;

        public OperatorBroadcaster(IHubContext<OperatorHub> ctx)
        {
            _ctx = ctx;
            Instance = this;
            AdminHub.ClientUpdated += OnClientUpdated;
            AdminHub.ClientsChanged += OnClientsChanged;
            AdminHub.ClientOfflineWithSession += OnClientOffline;
        }

        private void OnClientUpdated(ClientState cs)
        {
            _ = _ctx.Clients.All.SendAsync("pcUpdated", ClientDto(cs));
        }

        private void OnClientsChanged()
        {
            var all = AdminHub.KnownClients.Values.Select(ClientDto).ToList();
            _ = _ctx.Clients.All.SendAsync("allPcsUpdated", all);
        }

        private void OnClientOffline(ClientState cs)
        {
            _ = _ctx.Clients.All.SendAsync("offlineAlert", new
            {
                pcNumber = cs.PcNumber,
                sessionType = cs.SessionType,
                elapsed = cs.ElapsedAtDisconnect
            });
        }

        public void NotifyOfflineResolved(string pcNumber, string decision)
        {
            _ = _ctx.Clients.All.SendAsync("offlineResolved", new { pcNumber, decision });
        }

        public static object ClientDto(ClientState cs) => new
        {
            pcNumber = cs.PcNumber,
            pcNumberValue = cs.PcNumberValue,
            status = cs.Status,
            sessionType = cs.SessionType,
            isOnline = cs.IsOnline,
            isSession = cs.IsSession,
            isPaused = cs.IsPaused,
            isLocked = cs.IsLocked,
            isFree = cs.IsFree,
            elapsedSeconds = cs.ElapsedSeconds,
            limitSeconds = cs.LimitSeconds,
            paidAmount = cs.PaidAmount,
            accumulatedSeconds = cs.AccumulatedSeconds,
            sessionStart = cs.SessionStart?.ToString("o"),
            userName = cs.UserName,
            readerId = cs.ReaderId,
            ip = cs.Ip,
            disconnectedAt = cs.DisconnectedAt?.ToString("o"),
            elapsedAtDisconnect = cs.ElapsedAtDisconnect
        };
    }
}
