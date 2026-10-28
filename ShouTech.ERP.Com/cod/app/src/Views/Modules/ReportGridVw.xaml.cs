// ========================================================================
// FILE: Views/Modules/ReportGridVw.xaml.cs
// PROJECT: SHOUTECH ERP — Excel-like report grid view
// ========================================================================

#nullable enable

using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows;
using ShouTech.App.Reporting;

namespace ShouTech.App.Views.Modules
{
    public partial class ReportGridVw : UserControl
    {
        private ReportDesignerVM? _vm;
        private bool _rebuilding;

        public ReportGridVw()
        {
            InitializeComponent();
        }

        public void Bind(ReportDesignerVM vm)
        {
            _vm = vm;
            DataContext = vm;
            vm.GridRebuildRequested += (_, _) => RebuildColumns();
            vm.PropertyChanged += Vm_PropertyChanged;
            RebuildColumns();
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReportDesignerVM.IsEditMode))
            {
                var editing = _vm?.IsEditMode == true;
                DesignerPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                DesignerColumn.Width = editing ? new GridLength(320) : new GridLength(0);

                if (!editing)
                    RebuildColumns();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════════

        private void RebuildColumns()
        {
            if (_vm is null || _rebuilding)
                return;

            _rebuilding = true;
            try
            {
                ReportGrid.Columns.Clear();
                ReportGrid.FrozenColumnCount =
                    _vm.Definition.FreezeFirstColumn && _vm.Definition.VisibleColumns.Any() ? 1 : 0;

                foreach (var col in _vm.Definition.VisibleColumns)
                {
                    var gridCol = new DataGridTextColumn
                    {
                        Header = string.IsNullOrWhiteSpace(col.CaptionAR)
                            ? col.FieldKey : col.CaptionAR,
                        Width = new DataGridLength(col.ColumnWidth, DataGridLengthUnitType.Pixel),
                        CanUserResize = !col.IsLocked,
                        Binding = CreateBinding(col)
                    };

                    var descriptor = DependencyPropertyDescriptor.FromProperty(
                        DataGridColumn.WidthProperty, typeof(DataGridColumn));
                    var fieldKey = col.FieldKey;
                    descriptor?.AddValueChanged(gridCol, (_, _) =>
                    {
                        if (_rebuilding || _vm is null)
                            return;
                        var actual = gridCol.Width.UnitType == DataGridLengthUnitType.Pixel
							? gridCol.Width.DisplayValue
							: gridCol.ActualWidth;
                        _vm.CommitColumnWidth(fieldKey, actual);
                    });

                    if (col.DataFormat is ReportDataFormat.Money or ReportDataFormat.Qty
                        or ReportDataFormat.Percent)
                    {
                        var style = new Style(typeof(TextBlock));
                        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                        style.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight));
                        if (col.NegativeInRed)
                        {
                            style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                                ShouTech.App.Services.VisuId.ResolveBrushResource("TextPrimary", "#202124")));
                        }
                        gridCol.ElementStyle = style;
                    }

                    ReportGrid.Columns.Add(gridCol);
                }
            }
            finally
            {
                _rebuilding = false;
            }
        }

        private static Binding CreateBinding(ReportColumnDef col)
        {
            var binding = new Binding($"[{col.FieldKey}]");

            switch (col.DataFormat)
            {
                case ReportDataFormat.Money:
                    binding.StringFormat = $"N{col.DecimalPlaces}";
                    break;
                case ReportDataFormat.Qty:
                    binding.StringFormat = $"N{Math.Min(col.DecimalPlaces, 4)}";
                    break;
                case ReportDataFormat.Percent:
                    binding.StringFormat = $"P{col.DecimalPlaces}";
                    break;
                case ReportDataFormat.Date:
                    binding.StringFormat = "yyyy-MM-dd";
                    break;
                case ReportDataFormat.DateTime:
                    binding.StringFormat = "yyyy-MM-dd HH:mm";
                    break;
                default:
                    binding.TargetNullValue = string.Empty;
                    break;
            }

            return binding;
        }

        // ═══════════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════════

        private void EditModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _vm?.ToggleEditModeCommand.Execute(null);
            EditModeToggle.IsChecked = _vm?.IsEditMode == true;
        }

        private void SaveAsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAsNameBox.Text = string.Empty;
            SaveAsOverlay.Visibility = Visibility.Visible;
            SaveAsNameBox.Focus();
        }

        private void SaveAsCancel_Click(object sender, RoutedEventArgs e)
        {
            SaveAsOverlay.Visibility = Visibility.Collapsed;
        }

        private void SaveAsConfirm_Click(object sender, RoutedEventArgs e)
        {
            var name = SaveAsNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name) || _vm is null)
                return;

            _vm.SaveAsCommand.Execute(name);
            SaveAsOverlay.Visibility = Visibility.Collapsed;
        }
    }
}
