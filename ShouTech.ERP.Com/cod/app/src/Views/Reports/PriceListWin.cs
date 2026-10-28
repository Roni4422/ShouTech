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
/// Phase 7B - "Price list" (Inventory > Movements).
/// Lists every material with its full price ladder (retail, wholesale,
/// special, minimum) and price currency, so the sales price catalogue
/// can be reviewed and printed per group or material.
/// </summary>
public sealed class PriceListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.PriceList",
        TitleTextKey = "Inventory.PriceList",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "MatCard.Code", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "MatCard.Name", Type = RptColType.Text, Width = 250 },
            new() { Key = "Unit", HeaderTextKey = "Reports.Col.Unit", Type = RptColType.Text, Width = 80 },
            new() { Key = "Retail", HeaderTextKey = "MatCard.PriceRetail", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Wholesale", HeaderTextKey = "MatCard.PriceWholesale", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Special", HeaderTextKey = "MatCard.PriceSpecial", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Min", HeaderTextKey = "MatCard.PriceMin", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 80 },
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

        IEnumerable<MatRepo.MatRec> materials = await Task.Run(() => MatRepo.LoadAll(), ct);

        if (search.Length > 0)
        {
            materials = materials.Where(m =>
                m.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
             || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
             || (m.NameEn ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (group.Length > 0)
            materials = materials.Where(m => string.Equals(m.GroupCode, group, StringComparison.Ordinal));

        var rows = materials
            .OrderBy(m => m.Code)
            .Select(m => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = m.Code,
                ["Name"] = isArabic || string.IsNullOrWhiteSpace(m.NameEn) ? m.Name : m.NameEn,
                ["Unit"] = m.DefaultUnit ?? m.Unit1 ?? string.Empty,
                ["Retail"] = m.PriceRetail,
                ["Wholesale"] = m.PriceWholesale,
                ["Special"] = m.PriceSpecial,
                ["Min"] = m.PriceMin,
                ["Currency"] = m.PriceCurrency ?? string.Empty,
            })
            .ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Retail"] = rows.Sum(x => ToDec(x["Retail"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}
