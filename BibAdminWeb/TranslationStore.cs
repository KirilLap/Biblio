using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BibAdminWeb
{
    /// <summary>
    /// Правки узбекского перевода интерфейса оператора, сделанные в админке
    /// (Настройки → Переводы). Хранятся только строки, отличающиеся от встроенного
    /// словаря wwwroot\i18n-uz.js. Файл лежит в data\ рядом с остальными данными,
    /// поэтому при обновлениях не затирается.
    /// </summary>
    public static class TranslationStore
    {
        private static readonly string FilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "translations_uz.json");

        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _fileJson = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static Dictionary<string, string> Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка чтения переводов: {ex.Message}");
                    return new();
                }
            }
        }

        public static void Save(Dictionary<string, string> map)
        {
            var clean = new Dictionary<string, string>();
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    clean[kv.Key] = kv.Value;

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(clean, _fileJson));
            }
            Logger.Info($"🌐 Переводы сохранены: изменённых строк — {clean.Count}");
        }

        /// <summary>Скрипт для страниц оператора: window.I18N_UZ_OVERRIDES = {...};</summary>
        public static string AsScript()
            => "window.I18N_UZ_OVERRIDES = " + JsonSerializer.Serialize(Load()) + ";";
    }
}
