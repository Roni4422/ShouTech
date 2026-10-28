using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// Phase 7B - "Material list" / "Search materials" (Inventory > Material reports).
/// Lists every material of the current company with its group, unit,
/// barcode and retail price. Serves as the searchable material catalogue.
/// </summary>
public sealed class MatListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.MaterialList",
        TitleTextKey = "Inventory.MaterialList",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "MatCard.Code", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "MatCard.Name", Type = RptColType.Text, Width = 260 },
            new() { Key = "Group", HeaderTextKey = "MatCard.Group", Type = RptColType.Text, Width = 110 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Barcode", HeaderTextKey = "MatCard.Barcode", Type = RptColType.Text, Width = 150 },
            new() { Key = "Price", HeaderTextKey = "MatCard.PriceRetail", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Material", LabelTextKey = "Inventory.MaterialFilter", Kind = RptFltKind.Material },
            new() { Key = "Group", LabelTextKey = "Inventory.MaterialGroupFilter", Kind = RptFltKind.MaterialGroup },
        }
    };

    protected override IReadOnlyList<KeyValuePair<string, string>> LoadMaterialGroupChoices()
    {
        var choices = new List<KeyValuePair<string, string>>
        {
            new("", T("Reports.Filter.All"))
        };
        choices.AddRange(MatRepo.LoadGroups()
            .Where(group => !group.IsDeleted)
            .Select(group => new KeyValuePair<string, string>(
                group.Code,
                string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                    ? group.Name
                    : string.IsNullOrWhiteSpace(group.NameEn) ? group.Name : group.NameEn)));
        return choices;
    }

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var search = (filters.GetString("Material") ?? string.Empty).Trim();
        var group = filters.GetString("Group") ?? string.Empty;

        var isArabic = string.Equals(
            App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);

        // Both lookups run off the UI thread.
        var groupNames = await Task.Run(() => MatRepo.LoadGroups()
            .Where(g => !g.IsDeleted)
            .ToDictionary(
                g => g.Code,
                g => isArabic || string.IsNullOrWhiteSpace(g.NameEn) ? g.Name : g.NameEn), ct);

        IEnumerable<MatRepo.MatRec> materials = await Task.Run(() => MatRepo.LoadAll(), ct);

        if (search.Length > 0)
        {
            materials = materials.Where(m =>
                m.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.NameEn ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.SearchName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (group.Length > 0)
            materials = materials.Where(m => string.Equals(m.GroupCode, group, StringComparison.Ordinal));

        var rows = materials
            .OrderBy(m => m.Code)
            .Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = m.Code,
                ["Name"] = isArabic || string.IsNullOrWhiteSpace(m.NameEn) ? m.Name : m.NameEn,
                ["Group"] = string.IsNullOrEmpty(m.GroupCode)
                    ? string.Empty
                    : groupNames.GetValueOrDefault(m.GroupCode, m.GroupCode),
                ["Unit"] = m.DefaultUnit ?? m.Unit1 ?? string.Empty,
                ["Barcode"] = m.Unit1Barcode ?? string.Empty,
                ["Price"] = m.PriceRetail,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Price"] = rows.Sum(x => ToDec(x["Price"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
