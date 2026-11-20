using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Help window: renders the shipped English user guide
/// (docs/USER_GUIDE_EN.md, with docs/SHOUTECH_ERP_EN.md as a legacy fallback)
/// as a readable FlowDocument. The section list navigates to headings.
/// If neither manual is present in the deployment, the window reports that
/// accurately instead of showing placeholder content.
/// </summary>
public sealed class HelpWin : BaseWin
{
    private readonly ListBox _sections = new();
    private readonly RichTextBox _reader = new();
    private readonly Dictionary<string, Paragraph> _headingParagraphs = new(StringComparer.Ordinal);
    private readonly TextBlock _statusBar = new();
    private readonly Button _closeButton = new();

    public HelpWin()
    {
        Width = 1080;
        Height = 680;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Help.View))
        {
            MessageBox.Show(this, T("Help.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        LoadManual();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_closeButton, "Common.Close");
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_closeButton));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _sections.Margin = new Thickness(0, 0, 10, 0);
        _sections.SelectionChanged += (_, _) =>
        {
            if (_sections.SelectedItem is string title && _headingParagraphs.TryGetValue(title, out var paragraph))
                paragraph.BringIntoView();
        };
        Grid.SetColumn(_sections, 0);
        content.Children.Add(_sections);

        _reader.IsReadOnly = true;
        _reader.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetColumn(_reader, 1);
        content.Children.Add(_reader);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Help.Title");
        if (_closeButton.Tag is string key)
            _closeButton.Content = T(key);
    }

    private void LoadManual()
    {
        var path = FindManualPath();
        if (path is null)
        {
            _statusBar.Text = T("Help.ManualNotFound");
            _reader.Document = new FlowDocument(new Paragraph(new Run(T("Help.ManualNotFound"))));
            return;
        }

        var document = new FlowDocument();
        var sections = new List<string>();
        _headingParagraphs.Clear();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal) ||
                line.StartsWith("### ", StringComparison.Ordinal))
            {
                var level = line.StartsWith("### ", StringComparison.Ordinal) ? 3 : line.StartsWith("## ", StringComparison.Ordinal) ? 2 : 1;
                var text = line[(level + 1)..].Trim();
                var paragraph = new Paragraph(new Run(text))
                {
                    FontSize = level == 1 ? 20 : level == 2 ? 16 : 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, level == 1 ? 14 : 10, 0, 4)
                };
                document.Blocks.Add(paragraph);
                if (!_headingParagraphs.ContainsKey(text))
                {
                    _headingParagraphs[text] = paragraph;
                    sections.Add(text);
                }
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run("• " + line[2..].Trim()))
                {
                    Margin = new Thickness(14, 0, 0, 2)
                });
            }
            else if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("```", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run(StripInline(line)))
                {
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
        }

        _reader.Document = document;
        _sections.ItemsSource = sections;
        _statusBar.Text = path;
    }

    private static string StripInline(string line)
        => line.Replace("**", string.Empty).Replace("`", string.Empty).Trim();

    private static string? FindManualPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "USER_GUIDE_EN.md");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "ShouTech.ERP.Com", "docs", "USER_GUIDE_EN.md");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "docs", "SHOUTECH_ERP_EN.md");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(dir.FullName, "ShouTech.ERP.Com", "docs", "SHOUTECH_ERP_EN.md");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
