using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class AdminBroadcaster
    {
        public static AdminBroadcaster? Instance { get; private set; }
        private readonly IHubContext<AdminWebHub> _ctx;

        public AdminBroadcaster(IHubContext<AdminWebHub> ctx)
        {
            _ctx = ctx;
            Instance = this;
            AdminHub.ClientUpdated += cs => _ = _ctx.Clients.All.SendAsync("pcUpdated", AdminWebHub.ClientDto(cs));
            AdminHub.ClientsChanged += () =>
            {
                var all = AdminHub.KnownClients.Values.Select(AdminWebHub.ClientDto).ToList();
                _ = _ctx.Clients.All.SendAsync("allPcsUpdated", all);
            };
            AdminHub.ClientOfflineWithSession += cs => _ = _ctx.Clients.All.SendAsync("offlineAlert", new
            {
                pcNumber = cs.PcNumber, sessionType = cs.SessionType, elapsed = cs.ElapsedAtDisconnect
            });
            AdminHub.ClientTimeDrift += (pcNumber, offsetSeconds) =>
                _ = _ctx.Clients.All.SendAsync("clockDriftAlert", new { pcNumber, offsetSeconds });
            AdminHub.ClientTimeMismatch += (pcNumber, clientSecs, serverSecs) =>
                _ = _ctx.Clients.All.SendAsync("timeMismatchAlert", new { pcNumber, clientSecs, serverSecs });
            AdminHub.ClientNameConflict += (registeredAs, requestedAs, mac, pcNumberValue, customName) =>
                _ = _ctx.Clients.All.SendAsync("nameConflictAlert", new { registeredAs, requestedAs, mac, pcNumberValue, customName });
        }

        public void NotifyOfflineResolved(string pcNumber, string decision)
            => _ = _ctx.Clients.All.SendAsync("offlineResolved", new { pcNumber, decision });

        public void PushSettings(GlobalSettings settings)
        {
            _ = _ctx.Clients.All.SendAsync("settingsUpdated", settings);
            var services = settings.Services
                .Where(s => s.IsActive)
                .Select(s => new { id = s.Id, name = s.Name, unit = s.Unit, price = s.Price })
                .ToList();
            OperatorBroadcaster.Instance?.PushServiceTypes();
        }
    }
}
