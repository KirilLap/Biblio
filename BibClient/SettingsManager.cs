using System;
using System.IO;
using System.Text.Json;

namespace BibClient
{
    public static class SettingsManager
    {
        private static readonly string _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        public static ClientSettings Current { get; set; } = new ClientSettings();

        public static void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<ClientSettings>(json);
                    if (loaded != null) Current = loaded;
                }
            }
            catch { Current = new ClientSettings(); }
        }

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }
}
