using System.Collections.ObjectModel;
using System.Windows.Input;
using System;

namespace ShouTech.App.ViewModels
{
    /// <summary>
    /// </summary>
    public sealed class RibbonTabViewModel : BaseVM
    {
        private string _header = string.Empty;
        private bool _isVisible = true;
        private bool _isSelected;

        public string Key { get; }

        public string Header
        {
            get => _header;
            set => SetProperty(ref _header, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ObservableCollection<RibbonGroupViewModel> Groups { get; } = new();

        public RibbonTabViewModel(string key, string header)
        {
            Key = string.IsNullOrWhiteSpace(key)
                ? throw new ArgumentException("Ribbon tab key must not be empty.", nameof(key))
                : key;
            _header = header ?? string.Empty;
        }
    }

    public sealed class RibbonGroupViewModel : BaseVM
    {
        private string _header = string.Empty;

        public string Key { get; }

        public string Header
        {
            get => _header;
            set => SetProperty(ref _header, value);
        }

        public ObservableCollection<RibbonItemViewModel> Items { get; } = new();

        public RibbonGroupViewModel(string key, string header)
        {
            Key = string.IsNullOrWhiteSpace(key)
                ? throw new ArgumentException("Ribbon group key must not be empty.", nameof(key))
                : key;
            _header = header ?? string.Empty;
        }
    }

    public sealed class RibbonItemViewModel : BaseVM
    {
        private string _label = string.Empty;
        private bool _isEnabled = true;
        private string? _iconKey;

        public string Key { get; }

        public string Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }

        public string? IconKey
        {
            get => _iconKey;
            set => SetProperty(ref _iconKey, value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public ICommand? Command { get; set; }

        public RibbonItemViewModel(string key, string label)
        {
            Key = string.IsNullOrWhiteSpace(key)
                ? throw new ArgumentException("Ribbon item key must not be empty.", nameof(key))
                : key;
            _label = label ?? string.Empty;
        }
    }
}
