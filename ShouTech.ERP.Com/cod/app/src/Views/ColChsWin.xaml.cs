using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

using ShouTech.App.Services;
namespace ShouTech.App.Views;

/// <summary>
/// Column chooser dialog shared by all report windows.
/// Lets the user toggle column visibility and reorder columns, then returns
/// the resulting layout. All captions come from localization resources.
/// </summary>
public partial class ColChsWin : BaseWin
{
    /// <summary>Editable column entry bound to the dialog list.</summary>
    public sealed class Item : INotifyPropertyChanged
    {
        private bool _visible;

        public required string Key { get; init; }
        public required string Header { get; init; }
        public bool Locked { get; init; }

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Item> _items = new();

    protected override bool PersistGeometry => false;

    public ColChsWin(IEnumerable<Item> columns)
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        foreach (var c in columns)
            _items.Add(c);
        LstColumns.ItemsSource = _items;
    }

    /// <summary>The resulting ordered layout after the dialog closes with OK.</summary>
    public IReadOnlyList<Item> Result => _items.ToList();

    protected override void ApplyLocalizedText()
    {
        Title = T("Report.Columns.Title");
        TxtTitle.Text = T("Report.Columns.Title");
        TxtHint.Text = T("Report.Columns.Hint");
        BtnUp.ToolTip = T("Common.MoveUp");
        BtnDown.ToolTip = T("Common.MoveDown");
        BtnSelectAll.Content = T("Common.SelectAll");
        BtnClearAll.Content = T("Common.ClearAll");
        BtnOk.Content = T("Common.OK");
        BtnCancel.Content = T("Common.Cancel");
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // At least one column must remain visible.
        if (_items.All(i => !i.Visible))
        {
            ShowWarn("Report.Columns.AtLeastOne");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void BtnUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void BtnDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        var index = LstColumns.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _items.Count)
            return;

        _items.Move(index, target);
        LstColumns.SelectedIndex = target;
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.Visible = true;
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.Visible = item.Locked;
    }
}
