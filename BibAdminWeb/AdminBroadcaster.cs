using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class AdminBroadcaster
    {
        public static AdminBroadcaster? Instance { get; private set; }
        private readonly IHubContext<AdminWebHub> _ctx;
        private readonly IHubContext<AdminHub> _adminCtx;

        public AdminBroadcaster(IHubContext<AdminWebHub> ctx, IHubContext<AdminHub> adminCtx)
        {
            _ctx = ctx;
            _adminCtx = adminCtx;
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
            // Уведомить браузер-клиентов (admin UI)
            _ = _ctx.Clients.All.SendAsync("settingsUpdated", settings);

            // Отправить новые настройки всем подключённым ПК-клиентам
            var commands = settings.ToCommands();
            foreach (var client in AdminHub.KnownClients.Values.Where(c => c.IsOnline))
            {
                foreach (var cmd in commands)
                {
                    if (client.IsIndividual(cmd.Type)) continue;
                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    _ = _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }
            }

            // Обновить список услуг у операторов
            OperatorBroadcaster.Instance?.PushServiceTypes();
        }
    }
}
