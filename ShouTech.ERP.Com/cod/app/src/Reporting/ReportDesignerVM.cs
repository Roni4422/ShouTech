// ========================================================================
// FILE: Reporting/ReportDesignerVM.cs
// PROJECT: SHOUTECH ERP — Excel-like report designer ViewModel
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System;
using CommunityToolkit.Mvvm.Input;

namespace ShouTech.App.Reporting
{
    public sealed class ReportRow : Dictionary<string, object?>, IRowValueProvider
    {
        public new bool TryGetValue(string columnKey, out object? value)
			=> base.TryGetValue(columnKey, out value);
    }

    public sealed class EditableColumn : INotifyPropertyChanged
    {
        private readonly ReportColumnDef _def;
        private string _formulaError = string.Empty;

        public ReportColumnDef Def => _def;

        public EditableColumn(ReportColumnDef def) => _def = def;

        public string FieldKey => _def.FieldKey;

        public string Caption
        {
            get => _def.CaptionAR;
            set { _def.CaptionAR = value; OnChanged(); }
        }

        public bool IsVisible
        {
            get => _def.IsVisible;
            set { _def.IsVisible = value; OnChanged(); }
        }

        public double Width
        {
            get => _def.ColumnWidth;
            set
            {
                _def.ColumnWidth = Math.Clamp(value, 30, 2000);
                OnChanged();
            }
        }

        public ReportColumnType ColumnType
        {
            get => _def.ColumnType;
            set { _def.ColumnType = value; OnChanged(); OnChanged(nameof(IsFormula)); }
        }

        public bool IsFormula => _def.ColumnType == ReportColumnType.Formula;

        public string FormulaText
        {
            get => _def.FormulaText ?? string.Empty;
            set
            {
                _def.FormulaText = value;
                FormulaError = string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : CompiledFormula.TryCompile(value, out var err) ? string.Empty : err!;
                OnChanged();
            }
        }

        public string FormulaError
        {
            get => _formulaError;
            private set { _formulaError = value; OnChanged(); OnChanged(nameof(HasFormulaError)); }
        }

        public bool HasFormulaError => !string.IsNullOrEmpty(_formulaError);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// </summary>
    public sealed class ReportDesignerVM : INotifyPropertyChanged
    {
        private readonly Guid _companyId;
        private readonly int _userId;
        private ReportDefinition _definition;
        private bool _isEditMode;
        private string _statusMessage = string.Empty;
        private bool _isBusy;

        public ReportDesignerVM(ReportDefinition definition, Guid companyId, int userId)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _companyId = companyId;
            _userId = userId;

            ToggleEditModeCommand = new RelayCommand(ToggleEditMode);
            AddFormulaColumnCommand = new RelayCommand(AddFormulaColumn);
            RemoveColumnCommand = new RelayCommand<EditableColumn>(RemoveColumn);
            MoveColumnUpCommand = new RelayCommand<EditableColumn>(c => MoveColumn(c, -1));
            MoveColumnDownCommand = new RelayCommand<EditableColumn>(c => MoveColumn(c, +1));
            SaveCommand = new AsyncRelayCommand(SaveAsync);
            SaveAsCommand = new AsyncRelayCommand<string>(SaveAsAsync);

            RebuildEditableColumns();
        }


        public ReportDefinition Definition => _definition;
        public ObservableCollection<ReportRow> Rows { get; } = new();
        public ObservableCollection<EditableColumn> EditableColumns { get; } = new();

        public bool IsEditMode
        {
            get => _isEditMode;
            private set { _isEditMode = value; OnChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnChanged(); }
        }

        public string ReportName
        {
            get => _definition.ReportName;
            set { _definition.ReportName = value; OnChanged(); }
        }

        public bool IsOwner => _definition.OwnerUserID == _userId && !_definition.IsSystem;


        public ICommand ToggleEditModeCommand { get; }
        public ICommand AddFormulaColumnCommand { get; }
        public ICommand RemoveColumnCommand { get; }
        public ICommand MoveColumnUpCommand { get; }
        public ICommand MoveColumnDownCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand SaveAsCommand { get; }

        public event EventHandler? GridRebuildRequested;

        // ═══════════════════════════════════════════════════════════════
        // Data loading & formula computation
        // ═══════════════════════════════════════════════════════════════

        public void SetData(IEnumerable<Dictionary<string, object?>> rawRows)
        {
            Rows.Clear();

            foreach (var raw in rawRows)
            {
                var row = new ReportRow();
                foreach (var kv in raw)
                    row[kv.Key] = kv.Value;
                Rows.Add(row);
            }

            ComputeFormulaColumns();
        }

        public void ComputeFormulaColumns()
        {
            var formulaCols = _definition.Columns
                .Where(c => c.ColumnType == ReportColumnType.Formula
                            && !string.IsNullOrWhiteSpace(c.FormulaText))
                .ToList();

            if (formulaCols.Count == 0)
                return;

            var compiled = new List<(string Key, CompiledFormula Formula)>();
            foreach (var col in formulaCols)
            {
                try
                {
                    compiled.Add((col.FieldKey, col.GetCompiled()!));
                }
                catch (FormulaException ex)
                {
                    StatusMessage = $"خطأ في معادلة العمود «{col.CaptionAR}»: {ex.Message}";
                }
            }

            foreach (var row in Rows)
            {
                foreach (var (key, formula) in compiled)
                    row[key] = formula.Evaluate(row);
            }
        }

        public void CommitColumnWidth(string fieldKey, double newWidth)
        {
            var col = _definition.Columns.FirstOrDefault(
                c => string.Equals(c.FieldKey, fieldKey, StringComparison.OrdinalIgnoreCase));
            if (col is not null)
                col.ColumnWidth = Math.Clamp(newWidth, 30, 2000);
        }

        // ═══════════════════════════════════════════════════════════════
        // Edit mode actions
        // ═══════════════════════════════════════════════════════════════

        private void ToggleEditMode()
        {
            IsEditMode = !IsEditMode;
            if (!IsEditMode)
            {
                RenumberColumns();
                ComputeFormulaColumns();
                GridRebuildRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AddFormulaColumn()
        {
            var index = 1;
            string key;
            do { key = $"CALC_{index++}"; }
            while (_definition.Columns.Any(c => c.FieldKey == key));

            var col = new ReportColumnDef
            {
                FieldKey = key,
                CaptionAR = $"عمود محسوب {index - 1}",
                ColumnType = ReportColumnType.Formula,
                FormulaText = string.Empty,
                DataFormat = ReportDataFormat.Money,
                ColumnWidth = 120,
                IsVisible = true,
                DisplayOrder = _definition.Columns.Count
            };

            _definition.Columns.Add(col);
            RebuildEditableColumns();
        }

        private void RemoveColumn(EditableColumn? editable)
        {
            if (editable is null || editable.Def.IsLocked)
                return;

            _definition.Columns.Remove(editable.Def);
            RenumberColumns();
            RebuildEditableColumns();
        }

        private void MoveColumn(EditableColumn? editable, int delta)
        {
            if (editable is null)
                return;

            var ordered = _definition.Columns.OrderBy(c => c.DisplayOrder).ToList();
            var idx = ordered.IndexOf(editable.Def);
            var target = idx + delta;
            if (idx < 0 || target < 0 || target >= ordered.Count)
                return;

            (ordered[idx].DisplayOrder, ordered[target].DisplayOrder) =
                (ordered[target].DisplayOrder, ordered[idx].DisplayOrder);

            RebuildEditableColumns();
            GridRebuildRequested?.Invoke(this, EventArgs.Empty);
        }

        private void RenumberColumns()
        {
            var order = 0;
            foreach (var col in _definition.Columns.OrderBy(c => c.DisplayOrder))
                col.DisplayOrder = order++;
        }

        private void RebuildEditableColumns()
        {
            EditableColumns.Clear();
            foreach (var col in _definition.Columns.OrderBy(c => c.DisplayOrder))
                EditableColumns.Add(new EditableColumn(col));
        }

        // ═══════════════════════════════════════════════════════════════
        // Persistence
        // ═══════════════════════════════════════════════════════════════

        private async Task SaveAsync()
        {
            if (!IsOwner)
            {
                StatusMessage = "لا يمكنك تعديل هذا التقرير — استخدم «حفظ باسم».";
                return;
            }

            if (!ValidateBeforeSave())
                return;

            IsBusy = true;
            try
            {
                _definition.ReportDefID = await ReportRepo.SaveReportAsync(
                    _definition, _companyId, _userId);
                StatusMessage = "تم حفظ التقرير بنجاح.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"فشل الحفظ: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SaveAsAsync(string? newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
            {
                StatusMessage = "أدخل اسماً للتقرير الجديد.";
                return;
            }

            if (!ValidateBeforeSave())
                return;

            IsBusy = true;
            try
            {
                var clone = _definition.CloneForSaveAs(newName.Trim(), _userId);
                clone.ReportDefID = await ReportRepo.SaveReportAsync(clone, _companyId, _userId);

                _definition = clone;
                OnChanged(nameof(Definition));
                OnChanged(nameof(ReportName));
                OnChanged(nameof(IsOwner));
                StatusMessage = $"تم حفظ التقرير باسم «{newName.Trim()}».";
            }
            catch (Exception ex)
            {
                StatusMessage = $"فشل الحفظ باسم: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool ValidateBeforeSave()
        {
            var duplicate = _definition.Columns
                .GroupBy(c => c.FieldKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                StatusMessage = $"مفتاح عمود مكرر: {duplicate.Key}.";
                return false;
            }

            foreach (var col in _definition.Columns.Where(
                         c => c.ColumnType == ReportColumnType.Formula))
            {
                if (string.IsNullOrWhiteSpace(col.FormulaText))
                {
                    StatusMessage = $"العمود «{col.CaptionAR}» معادلته فارغة.";
                    return false;
                }
                if (!CompiledFormula.TryCompile(col.FormulaText, out var err))
                {
                    StatusMessage = $"معادلة العمود «{col.CaptionAR}» غير صالحة: {err}";
                    return false;
                }
            }

            return true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
