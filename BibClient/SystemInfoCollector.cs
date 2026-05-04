using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BibClient
{
    public static class SystemInfoCollector
    {
        public static SystemInfoDto Collect()
        {
            return new SystemInfoDto
            {
                HostName = Environment.MachineName,
                OsVersion = Environment.OSVersion.VersionString,
                LocalIp = GetLocalIp(),
                MacAddress = GetMacAddress(),
                DiskFreeGb = GetFreeDiskSpace(),
                UptimeHours = GetUptimeHours()
            };
        }

        private static string GetLocalIp()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("127."))?.ToString() ?? "Unknown";
            }
            catch { return "Error"; }
        }

        private static string GetMacAddress()
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            var physical = nic?.GetPhysicalAddress().ToString();
            if (string.IsNullOrEmpty(physical)) return "Unknown";
            return string.Join(":", Enumerable.Range(0, physical.Length / 2).Select(i => physical.Substring(i * 2, 2)));
        }

        private static double GetFreeDiskSpace()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                return Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024), 1);
            }
            catch { return 0; }
        }

        private static double GetUptimeHours()
        {
            return Math.Round(TimeSpan.FromMilliseconds(Environment.TickCount64).TotalHours, 1);
        }
    }

    public class SystemInfoDto
    {
        public string HostName { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public double DiskFreeGb { get; set; }
        public double UptimeHours { get; set; }
    }
}