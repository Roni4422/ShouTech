    private void InitCombos()
    {
        CmbItemType.ItemsSource = new[]
        {
            new MatComboOption("Stock",    L("MatCard.ItemType.Stock",    "Stock")),
            new MatComboOption("Service",  L("MatCard.ItemType.Service",  "Service")),
            new MatComboOption("NonStock", L("MatCard.ItemType.NonStock", "Non-stock")),
            new MatComboOption("Kit",      L("MatCard.ItemType.Kit",      "Kit"))
        };

        CmbStatus.ItemsSource = new[]
        {
            new MatComboOption("Active",   L("MatCard.Status.Active",   "Active")),
            new MatComboOption("Inactive", L("MatCard.Status.Inactive", "Inactive"))
        };

        CmbCostMethod.ItemsSource = new[]
        {
            new MatComboOption("WeightedAvg",  L("MatCard.CostMethod.WeightedAvg",  "Weighted Average")),
            new MatComboOption("FIFO",         L("MatCard.CostMethod.FIFO",         "FIFO")),
            new MatComboOption("LIFO",         L("MatCard.CostMethod.LIFO",         "LIFO")),
            new MatComboOption("Standard",     L("MatCard.CostMethod.Standard",     "Standard")),
            new MatComboOption("LastPurchase", L("MatCard.CostMethod.LastPurchase", "Last Purchase"))
        };

        CmbAbcClass.ItemsSource = new[]
        {
            new MatComboOption("A", "A"),
            new MatComboOption("B", "B"),
            new MatComboOption("C", "C")
        };

        CmbPriceCurrency.ItemsSource = new[]
        {
            new MatComboOption("SYP", "SYP"),
            new MatComboOption("USD", "USD"),
            new MatComboOption("EUR", "EUR"),
            new MatComboOption("TRY", "TRY"),
            new MatComboOption("SAR", "SAR"),
            new MatComboOption("AED", "AED")
        };

        CmbColor.ItemsSource = BuildColorOptions();
        CmbColor.SelectedIndex = 0;

        InitGroups();
        InitWarehouses();
        RefreshComboDisplays();
        InitCategories();
    }

    private void InitCategories(string? preferredValue = null)
    {
        preferredValue ??= GetComboKey(CmbCategory);

        List<MatRepo.ProductCategoryRec> categories = new();
        try
        {
            categories = MatRepo.LoadProductCategories() ?? new();
            if (categories.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[MatCard] Warning: MatRepo.LoadProductCategories() returned empty.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MatCard] Loaded {categories.Count} categories from database.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatCard] Error loading categories: {ex.Message}");
            categories = new();
        }

        var isArabic = string.Equals(App.CurrentLanguageCode?.Trim(), "ar", StringComparison.OrdinalIgnoreCase);
        _categoryOptions = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Id))
            .Select(category =>
            {
                var name = isArabic
                    ? (string.IsNullOrWhiteSpace(category.Name) ? category.NameEn : category.Name)
                    : (string.IsNullOrWhiteSpace(category.NameEn) ? category.Name : category.NameEn);
                var display = string.IsNullOrWhiteSpace(name)
                    ? category.Code
                    : string.IsNullOrWhiteSpace(category.Code) ? name : $"{category.Code} - {name}";
                return new MatCategoryOption(category.Id, display, category.Code, category.Name, category.NameEn);
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredValue) && !_categoryOptions.Any(option =>
                string.Equals(option.Key, preferredValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Code, preferredValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Name, preferredValue, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.NameEn, preferredValue, StringComparison.OrdinalIgnoreCase)))
        {
            _categoryOptions.Add(new MatCategoryOption(preferredValue, preferredValue));
        }

        CmbCategory.ItemsSource = _categoryOptions;
        if (_categoryOptions.Count == 0)
        {
            CmbCategory.SelectedIndex = -1;
            CmbCategory.Text = string.Empty;
            return;
        }

        SetCategory(preferredValue);
    }

    private void InitGroups()
    {
        var groups = new List<MatComboOption>();
        try
        {
            var list = MatRepo.LoadGroups();
            if (list != null && list.Count > 0)
            {
                var lang = App.CurrentLanguageCode?.Trim().ToLowerInvariant();
                foreach (var item in list)
                {
                    if (item == null || item.IsDeleted) continue;
                    var code = item.Code;
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var name = lang == "ar"
                        ? (string.IsNullOrWhiteSpace(item.Name) ? item.NameEn : item.Name)
                        : (string.IsNullOrWhiteSpace(item.NameEn) ? item.Name : item.NameEn);
                    var display = string.IsNullOrWhiteSpace(name) ? code : code + " - " + name;
                    groups.Add(new MatComboOption(code, display));
                }
            }

            if (groups.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[MatCard] Warning: MatRepo.LoadGroups() returned empty.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatCard] Error loading groups: {ex.Message}");
        }

        if (groups.Count == 0)
        {
            try
            {
                var materialGroups = MatRepo.LoadAll()
                    .Select(x => x.GroupCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .Select(x => new MatComboOption(x!, $"[Auto] {x}"))
                    .ToList();

                if (materialGroups.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[MatCard] Using fallback: found {materialGroups.Count} unique GroupCodes.");
                    groups = materialGroups;
                }
            }
            catch { groups = new(); }
        }

        CmbGroup.ItemsSource = groups;
        if (groups.Count == 0)
        {
            CmbGroup.SelectedIndex = -1;
            CmbGroup.Text = string.Empty;
        }
    }
