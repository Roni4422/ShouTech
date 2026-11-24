using System;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

using ShouTech.Domain;
namespace ShouTech.App.Views
{
    /// <summary>
    /// First-run language picker. English default.
    /// Selecting Arabic previews full-window RTL; other languages use LTR.
    /// </summary>
    public partial class Lang : Window
    {
        public string Selected { get; private set; } = "en";

        public Lang()
        {
            InitializeComponent();
            VisuId.ApplyWindow(this);
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cmbLanguage != null && cmbLanguage.Items.Count > 0)
                {
                    cmbLanguage.SelectedIndex = 0;
                    if (cmbLanguage.Items[0] is ComboBoxItem first)
                    {
                        first.IsSelected = true;
                        cmbLanguage.SelectedItem = first;
                    }
                }

                Selected = "en";
                ApplyWindowDirection("en");
                cmbLanguage?.Focus();
            }
            catch (Exception exL42)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL42, "Lang.xaml.OnLoaded");
                Selected = "en";
            }
        }

        private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var code = ReadSelectedCode();
                Selected = code;
                ApplyWindowDirection(code);
            }
            catch (Exception exL56)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL56, "Lang.xaml.OnLanguageSelectionChanged");
                // non-fatal
            }
        }

        /// <summary>
        /// Mirrors the entire language window for Arabic (RTL);
        /// LTR for English / French / Turkish.
        /// </summary>
        private void ApplyWindowDirection(string? languageCode)
        {
            try
            {
                // The button bar needs no code-side handling: its XAML
                // anchors it to the layout end (FlowAlign.AlignEnd) and
                // the grid mirrors with the window.
                LangDirSv.ApplyToWindow(this, languageCode);
            }
            catch (Exception exL83)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL83, "Lang.xaml.ApplyWindowDirection");
                // non-fatal
            }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            Selected = ReadSelectedCode();

            // Apply the selected language BEFORE closing the first-run dialog.
            // This guarantees that every subsequent startup window is created
            // against the selected localization context, even when WPF closes
            // the dialog before deferred UI callbacks have executed.
            try
            {
                App.SetApplicationLanguage(Selected);
            }
            catch (Exception exL101)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL101, "Lang.xaml.OnOk");
                // The startup pipeline will apply the language again after the
                // dialog closes; do not block first-run setup on a UI refresh.
            }

            ApplyWindowDirection(Selected);
            SafeClose(dialogResult: true);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Selected = "en";
            SafeClose(dialogResult: false);
        }

        private string ReadSelectedCode()
        {
            try
            {
                if (cmbLanguage?.SelectedItem is ComboBoxItem item &&
                    item.Tag is string code &&
                    !string.IsNullOrWhiteSpace(code))
                {
                    return code.Trim().ToLowerInvariant();
                }

                return cmbLanguage?.SelectedIndex switch
                {
                    1 => "ar",
                    2 => "fr",
                    3 => "tr",
                    _ => "en"
                };
            }
            catch (Exception exL136)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL136, "Lang.xaml.ReadSelectedCode");
                return "en";
            }
        }

        private void SafeClose(bool dialogResult)
        {
            try { DialogResult = dialogResult; } catch (Exception exL144) {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL144, "Lang.xaml.SafeClose"); /* not a dialog */ }
            try { Close(); } catch (Exception exL145) {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL145, "Lang.xaml.SafeClose"); }

            if (!dialogResult)
            {
                try { WpfApp.Current?.Shutdown(); } catch (Exception exL149) {
                    AppHost.Errors.Report(ErrorCodes.StartupFailed, exL149, "Lang.xaml.SafeClose"); }
            }
        }
    }
}
