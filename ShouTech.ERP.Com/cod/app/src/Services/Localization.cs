// ========================================================================
// FILE: Services/Localization.cs
// PROJECT: SHOUTECH ERP — Localization & language direction
// Merged: LangDirSv + ILocSvc/LocSvc
// Future modules can stay separate; this domain is one file.
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Markup;
using System.Windows;
using System.Windows.Media;
using System;

namespace ShouTech.App.Services
{
    // ---- Language direction (RTL/LTR) ----
public static class LangDirSv
    {
        private static readonly HashSet<string> RtlLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ar", "ku", "he", "fa", "ur"
        };

        public const string DefaultLanguageCode = "en";

        public static bool IsRtl(string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return false;

            var code = languageCode.Split('-')[0];
            return RtlLanguageCodes.Contains(code);
        }

        public static FlowDirection GetFlowDirection(string? languageCode)
            => IsRtl(languageCode) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        public static void ApplyCulture(string languageCode)
        {
            var normalized = NormalizeLanguageCode(languageCode);
            var culture = CultureInfo.GetCultureInfo(normalized);

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        public static void ApplyToWindow(Window window, string? languageCode = null)
		{
			if (window == null)
				return;

			var code = NormalizeLanguageCode(languageCode ?? App.CurrentLanguageCode);
			var isRtl = IsRtl(code);

			// Update application-wide alignment resources first.
			// These are consumed via DynamicResource by every open window
			// and by any window opened afterwards.
			UpdateApplicationAlignmentResources(isRtl);

			// Window flow direction only (Arabic => RTL). Children inherit; do not
			// force FlowDirection on every control (that breaks label/field grids).
			window.FlowDirection = GetFlowDirection(code);

			try
			{
				// Western digits (0-9) always — does not force LTR layout
				window.Language = XmlLanguage.GetLanguage("en-US");
				NumberSubstitution.SetCultureSource(window, NumberCultureSource.Override);
				NumberSubstitution.SetCultureOverride(window, CultureInfo.GetCultureInfo("en-US"));
				NumberSubstitution.SetSubstitution(window, NumberSubstitutionMethod.European);
			}
			catch (CultureNotFoundException) { }
		}

		/// <summary>
		/// Updates the alignment and margin resources consumed by the central
		/// styles (grid headers, grid cells, text boxes, labels) according to
		/// the current language direction.
		/// </summary>
		private static void UpdateApplicationAlignmentResources(bool isRtl)
		{
			var app = System.Windows.Application.Current;
			if (app == null) return;

			var alignment = isRtl ? TextAlignment.Right : TextAlignment.Left;

			app.Resources["FieldTextAlignment"]      = alignment;
			app.Resources["GridHeaderTextAlignment"] = alignment;
			app.Resources["GridCellTextAlignment"]   = alignment;

			app.Resources["LblMargin"] = isRtl
				? new Thickness(5, 0, 0, 0)
				: new Thickness(0, 0, 5, 0);

			app.Resources["FieldMargin"] = isRtl
				? new Thickness(12, 2, 0, 2)
				: new Thickness(0, 2, 12, 2);
		}

        public static void ApplyToAllWindows(string languageCode)
        {
            if (System.Windows.Application.Current is null)
                return;

            foreach (Window window in System.Windows.Application.Current.Windows)
                ApplyToWindow(window, languageCode);
        }

        public static string NormalizeLanguageCode(string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return DefaultLanguageCode;

            return languageCode.Split('-')[0].ToLowerInvariant();
        }
    }

    // ---- Localization service (ILocSvc / LocSvc) ----
/// <summary>
    /// </summary>
    public interface ILocSvc
    {
        /// <summary>
        /// Gets the localized value for a resource key.
        /// </summary>
        string GetString(string key);
        
        /// <summary>
        /// Gets the localized value or the supplied fallback value.
        /// </summary>
        string GetString(string key, string defaultValue);

        string GetString(string key, string languageCode, string defaultValue);
        
        /// <summary>
        /// Sets the current application language.
        /// </summary>
        void SetLanguage(string languageCode);
        
        /// <summary>
        /// Gets the current application language.
        /// </summary>
        string GetCurrentLanguage();
        
        /// <summary>
        /// Raised when the application language changes.
        /// </summary>
        event EventHandler? LanguageChanged;
        
        /// <summary>
        /// Gets the list of available language codes.
        /// </summary>
        List<string> GetAvailableLanguages();
        
        /// <summary>
        /// Loads localization resources for compatibility with existing callers.
        /// </summary>
        void LoadResources();
    }

    /// <summary>
    /// Loads and resolves localized resources from language JSON files.
    /// </summary>
    public class LocSvc : ILocSvc
    {
        private Dictionary<string, string> _resources = new();
        private readonly Dictionary<string, Dictionary<string, string>> _languageResources = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _labelToKey = new(StringComparer.Ordinal);
        private string _currentLanguage = "en";
        private readonly string _resourcesPath;
        private readonly List<string> _availableLanguages = new() { "ar", "en", "fr", "tr" };

        public event EventHandler? LanguageChanged;

        public LocSvc()
        {
            _resourcesPath = ResolveResourcesPath();
            BuildLabelIndex();
            LoadResources();
        }

        public bool TryResolveKeyFromLabel(string? label, out string key)
        {
            key = string.Empty;
            if (string.IsNullOrWhiteSpace(label))
                return false;

            var trimmed = label.Trim();
            if (_labelToKey.TryGetValue(trimmed, out key!))
                return true;

            var stripped = StripLeadingEmoji(trimmed);
            if (!string.IsNullOrWhiteSpace(stripped) && _labelToKey.TryGetValue(stripped.Trim(), out key!))
                return true;

            foreach (var prefix in ReportLabelPrefixes)
            {
                if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var withoutPrefix = trimmed[prefix.Length..].Trim();
                if (_labelToKey.TryGetValue(withoutPrefix, out key!))
                    return true;
            }

            return false;
        }

        private static readonly string[] ReportLabelPrefixes = { "Report ", "Reports " };

        private static string ResolveResourcesPath()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(basePath, "Resources", "Lang"),
                Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "Resources", "Lang")),
                Path.GetFullPath(Path.Combine(basePath, "..", "..", "Resources", "Lang"))
            };

            foreach (var path in candidates)
            {
                if (Directory.Exists(path))
                    return path;
            }

            var fallback = candidates[0];
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private void BuildLabelIndex()
        {
            _labelToKey.Clear();
            foreach (var filePath in Directory.EnumerateFiles(_resourcesPath, "*.json"))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(filePath));
                    if (data == null)
                        continue;

                    var flat = new Dictionary<string, string>();
                    FlattenToDictionary(data, string.Empty, flat);
                    foreach (var pair in flat)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Value) && !_labelToKey.ContainsKey(pair.Value))
                            _labelToKey[pair.Value] = pair.Key;
                    }
                }
                catch (JsonException)
                {
                    // A malformed optional language file must not prevent startup.
                }
            }
        }

        private static string StripLeadingEmoji(string value)
        {
            var i = 0;
            while (i < value.Length && !char.IsLetter(value[i]) && !char.IsDigit(value[i]))
                i++;

            return i < value.Length ? value[i..].Trim() : value.Trim();
        }

        private static void FlattenToDictionary(Dictionary<string, object> dict, string prefix, Dictionary<string, string> target)
        {
            foreach (var kvp in dict)
            {
                var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";

                if (kvp.Value is JsonElement jsonElement)
                {
                    if (jsonElement.ValueKind == JsonValueKind.Object)
                    {
                        var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                        if (nested != null)
                            FlattenToDictionary(nested, key, target);
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.String)
                    {
                        target[key] = jsonElement.GetString() ?? key;
                    }
                }
                else if (kvp.Value is string str)
                {
                    target[key] = str;
                }
                else if (kvp.Value is Dictionary<string, object> nestedDict)
                {
                    FlattenToDictionary(nestedDict, key, target);
                }
            }
        }

        public string GetString(string key)
        {
            return _resources.TryGetValue(key, out var value) ? value : key;
        }

        public string GetString(string key, string defaultValue)
        {
            return _resources.TryGetValue(key, out var value) ? value : defaultValue;
        }

        public string GetString(string key, string languageCode, string defaultValue)
        {
            var language = string.IsNullOrWhiteSpace(languageCode) ? _currentLanguage : languageCode.Trim().ToLowerInvariant();
            if (language.Length > 2)
                language = language[..2];

            if (!_languageResources.TryGetValue(language, out var resources))
            {
                resources = LoadLanguageResources(language);
                _languageResources[language] = resources;
            }

            return resources.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private Dictionary<string, string> LoadLanguageResources(string languageCode)
        {
            var resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var fileName = languageCode switch
                {
                    "ar" => "Ar.json",
                    "en" => "En.json",
                    "fr" => "Fr.json",
                    "tr" => "Tr.json",
                    _ => $"{char.ToUpper(languageCode[0], CultureInfo.InvariantCulture)}{languageCode[1..]}.json"
                };
                var filePath = Path.Combine(_resourcesPath, fileName);
                if (!File.Exists(filePath))
                    return resources;

                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(filePath));
                if (data != null)
                    FlattenToDictionary(data, string.Empty, resources);
            }
            catch
            {
                // A missing optional language resource falls back to the English source text.
            }
            return resources;
        }

        public void SetLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return;

            var normalized = languageCode.Trim().ToLowerInvariant();
            if (normalized.Length > 2)
                normalized = normalized[..2];

            if (!_availableLanguages.Contains(normalized))
                return;

            if (normalized == _currentLanguage)
            {
                LanguageChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _currentLanguage = normalized;
            LoadResources();
            
            try
            {
                var culture = new System.Globalization.CultureInfo(languageCode);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch (Exception)
            {
                // Keep current culture if the requested code is invalid.
            }

            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetCurrentLanguage() => _currentLanguage;

        public List<string> GetAvailableLanguages() => _availableLanguages;

        public void LoadResources()
        {
            LoadLanguage(_currentLanguage);
        }

        private void LoadLanguage(string languageCode)
        {
            try
            {
                var fileName = languageCode switch
                {
                    "ar" => "Ar.json",
                    "en" => "En.json",
                    "fr" => "Fr.json",
                    "tr" => "Tr.json",
                    _ => $"{char.ToUpper(languageCode[0], CultureInfo.InvariantCulture)}{languageCode.Substring(1)}.json"
                };
                
                var filePath = Path.Combine(_resourcesPath, fileName);
                
                if (!File.Exists(filePath))
                    CreateDefaultLanguageFile(filePath, languageCode);

                var json = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                
                if (data != null)
                {
                    _resources.Clear();
                    FlattenDictionary(data, "");
                }
            }
            catch (Exception)
            {
                // Keep previously loaded resources if reload fails.
            }
        }

        private static void CreateDefaultLanguageFile(string filePath, string languageCode)
        {
            try
            {
                var defaultContent = languageCode switch
                {
                    "ar" => @"{
  ""Application"": {
    ""Title"": ""شو تيك إي آر بي"",
    ""Subtitle"": ""منصة إدارة المؤسسات المتكاملة"",
    ""Version"": ""الإصدار"",
    ""Ready"": ""جاهز""
  },
  ""Menu"": {
    ""File"": ""ملف"",
    ""Inventory"": ""المستودعات"",
    ""Accounts"": ""الحسابات"",
    ""Reports"": ""تقارير"",
    ""Tools"": ""أدوات"",
    ""Help"": ""مساعدة"",
    ""Exit"": ""خروج""
  },
  ""Dashboard"": {
    ""Title"": ""لوحة القيادة"",
    ""Welcome"": ""مرحباً بك""
  },
  ""Status"": {
    ""Ready"": ""جاهز"",
    ""Connected"": ""متصل""
  },
  ""Buttons"": {
    ""Save"": ""حفظ"",
    ""Cancel"": ""إلغاء"",
    ""Delete"": ""حذف"",
    ""Edit"": ""تعديل"",
    ""Add"": ""إضافة"",
    ""Search"": ""بحث""
  }
}",
                    "en" => @"{
  ""Application"": {
    ""Title"": ""ShouTech ERP"",
    ""Subtitle"": ""Integrated Enterprise Management Platform"",
    ""Version"": ""Version"",
    ""Ready"": ""Ready""
  },
  ""Menu"": {
    ""File"": ""File"",
    ""Inventory"": ""Inventory"",
    ""Accounts"": ""Accounts"",
    ""Reports"": ""Reports"",
    ""Tools"": ""Tools"",
    ""Help"": ""Help"",
    ""Exit"": ""Exit""
  },
  ""Dashboard"": {
    ""Title"": ""Dashboard"",
    ""Welcome"": ""Welcome""
  },
  ""Status"": {
    ""Ready"": ""Ready"",
    ""Connected"": ""Connected""
  },
  ""Buttons"": {
    ""Save"": ""Save"",
    ""Cancel"": ""Cancel"",
    ""Delete"": ""Delete"",
    ""Edit"": ""Edit"",
    ""Add"": ""Add"",
    ""Search"": ""Search""
  }
}",
                    "fr" => @"{
  ""Application"": {
    ""Title"": ""ShouTech ERP"",
    ""Subtitle"": ""Plateforme de Gestion d'Entreprise Intégrée"",
    ""Version"": ""Version"",
    ""Ready"": ""Prêt""
  },
  ""Menu"": {
    ""File"": ""Fichier"",
    ""Inventory"": ""Inventaire"",
    ""Accounts"": ""Comptes"",
    ""Reports"": ""Rapports"",
    ""Tools"": ""Outils"",
    ""Help"": ""Aide"",
    ""Exit"": ""Quitter""
  },
  ""Dashboard"": {
    ""Title"": ""Tableau de Bord"",
    ""Welcome"": ""Bienvenue""
  },
  ""Status"": {
    ""Ready"": ""Prêt"",
    ""Connected"": ""Connecté""
  },
  ""Buttons"": {
    ""Save"": ""Enregistrer"",
    ""Cancel"": ""Annuler"",
    ""Delete"": ""Supprimer"",
    ""Edit"": ""Modifier"",
    ""Add"": ""Ajouter"",
    ""Search"": ""Rechercher""
  }
}",
                    "tr" => @"{
  ""Application"": {
    ""Title"": ""ShouTech ERP"",
    ""Subtitle"": ""Entegre Kurumsal Yönetim Platformu"",
    ""Version"": ""Sürüm"",
    ""Ready"": ""Hazır""
  },
  ""Menu"": {
    ""File"": ""Dosya"",
    ""Inventory"": ""Envanter"",
    ""Accounts"": ""Hesaplar"",
    ""Reports"": ""Raporlar"",
    ""Tools"": ""Araçlar"",
    ""Help"": ""Yardım"",
    ""Exit"": ""Çıkış""
  },
  ""Dashboard"": {
    ""Title"": ""Yönetici Paneli"",
    ""Welcome"": ""Hoş Geldiniz""
  },
  ""Status"": {
    ""Ready"": ""Hazır"",
    ""Connected"": ""Bağlı""
  },
  ""Buttons"": {
    ""Save"": ""Kaydet"",
    ""Cancel"": ""İptal"",
    ""Delete"": ""Sil"",
    ""Edit"": ""Düzenle"",
    ""Add"": ""Ekle"",
    ""Search"": ""Ara""
  }
}",
                    _ => @"{
  ""Application"": {
    ""Title"": ""ShouTech ERP""
  },
  ""Menu"": {
    ""File"": ""File"",
    ""Exit"": ""Exit""
  }
}"
                };
                
                File.WriteAllText(filePath, defaultContent);
            }
            catch (Exception)
            {
                // Non-fatal: caller will retry with empty resources.
            }
        }

        private void FlattenDictionary(Dictionary<string, object> dict, string prefix)
        {
            foreach (var kvp in dict)
            {
                var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";
                
                if (kvp.Value is JsonElement jsonElement)
                {
                    if (jsonElement.ValueKind == JsonValueKind.Object)
                    {
                        var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                        if (nested != null)
                        {
                            FlattenDictionary(nested, key);
                        }
                    }
                    else if (jsonElement.ValueKind == JsonValueKind.String)
                    {
                        _resources[key] = jsonElement.GetString() ?? key;
                    }
                    else
                    {
                        _resources[key] = jsonElement.ToString();
                    }
                }
                else if (kvp.Value is string str)
                {
                    _resources[key] = str;
                }
                else if (kvp.Value is Dictionary<string, object> nestedDict)
                {
                    FlattenDictionary(nestedDict, key);
                }
            }
        }
    }
}
