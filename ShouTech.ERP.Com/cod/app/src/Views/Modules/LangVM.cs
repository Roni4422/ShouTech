// ========================================================================
// FILE: Views/LangVM.cs
// PROJECT: SHOUTECH ERP
// PURPOSE: Language Selection ViewModel
// ========================================================================

#nullable enable

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;

namespace ShouTech.App.ViewModels
{
    public sealed class LangVM : INotifyPropertyChanged
    {
        private ObservableCollection<LangItem> _languages = new();

        private LangItem? _selectedLanguage;

        public ObservableCollection<LangItem> Languages
        {
            get => _languages;
            private set
            {
                if (ReferenceEquals(_languages, value))
                    return;

                _languages = value;
                OnPropertyChanged();
            }
        }

        public LangItem? SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (ReferenceEquals(_selectedLanguage, value))
                    return;

                _selectedLanguage = value;
                OnPropertyChanged();
            }
        }

        public LangVM()
        {
            LoadLanguages();
        }

        public void LoadLanguages(string? currentLanguage = null)
        {
            Languages = new ObservableCollection<LangItem>
            {
                new()
                {
                    Code = "ar",
                    DisplayName = "العربية",
                    NativeName = "العربية",
                    Icon = "🇸🇦"
                },

                new()
                {
                    Code = "en",
                    DisplayName = "English",
                    NativeName = "English",
                    Icon = "🇬🇧"
                },

                new()
                {
                    Code = "fr",
                    DisplayName = "Français",
                    NativeName = "Français",
                    Icon = "🇫🇷"
                },

                new()
                {
                    Code = "ku",
                    DisplayName = "کوردی",
                    NativeName = "کوردی",
                    Icon = "🌐"
                },

                new()
                {
                    Code = "tr",
                    DisplayName = "Türkçe",
                    NativeName = "Türkçe",
                    Icon = "🇹🇷"
                }
            };

            var normalized = NormalizeCode(currentLanguage);

            SelectedLanguage =
                Languages.FirstOrDefault(x => x.Code == normalized)
                ?? Languages.FirstOrDefault(x => x.Code == "en");
        }

        private static string NormalizeCode(string? code)
        {
            return (code ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "ar" or "ar-sa" => "ar",
                "en" or "en-us" => "en",
                "fr" or "fr-fr" => "fr",
                "ku" or "ku-tr" => "ku",
                "tr" or "tr-tr" => "tr",
                _ => "en"
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class LangItem
    {
        public string Code { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string NativeName { get; init; } = string.Empty;

        public string Icon { get; init; } = string.Empty;
    }
}