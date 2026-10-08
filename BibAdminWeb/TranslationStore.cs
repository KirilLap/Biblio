using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BibAdminWeb
{
    /// <summary>
    /// Правки текста интерфейса оператора, сделанные в админке (Настройки → Переводы):
    /// русского ("ru") и узбекского ("uz"). Хранятся только строки, отличающиеся от
    /// встроенных (русский — текст в коде, узбекский — wwwroot\i18n-uz.js).
    /// Файлы лежат в data\ рядом с остальными данными, поэтому при обновлениях не затираются.
    /// </summary>
    public static class TranslationStore
    {
        public static readonly string[] Langs = { "ru", "uz" };

        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _fileJson = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static bool IsKnownLang(string lang) => Array.IndexOf(Langs, lang) >= 0;

        private static string FilePath(string lang) => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", $"translations_{lang}.json");

        public static Dictionary<string, string> Load(string lang)
        {
            if (!IsKnownLang(lang)) return new();
            lock (_lock)
            {
                try
                {
                    var path = FilePath(lang);
                    if (!File.Exists(path)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка чтения переводов ({lang}): {ex.Message}");
                    return new();
                }
            }
        }

        public static void Save(string lang, Dictionary<string, string> map)
        {
            if (!IsKnownLang(lang)) return;
            var clean = new Dictionary<string, string>();
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    clean[kv.Key] = kv.Value;

            lock (_lock)
            {
                var path = FilePath(lang);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(clean, _fileJson));
            }
            Logger.Info($"🌐 Переводы ({lang}) сохранены: изменённых строк — {clean.Count}");
        }

        /// <summary>Скрипт для страниц оператора: window.I18N_RU_OVERRIDES / window.I18N_UZ_OVERRIDES.</summary>
        public static string AsScript()
            => "window.I18N_RU_OVERRIDES = " + JsonSerializer.Serialize(Load("ru")) + ";\n"
             + "window.I18N_UZ_OVERRIDES = " + JsonSerializer.Serialize(Load("uz")) + ";";
    }
}
