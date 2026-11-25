using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.App.Views;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Material-card persistence round-trip facts (owner audit, 2026-11-17):
    /// every field the card collects must come back from MatRepo.LoadById
    /// unchanged, and one material's child rows (specifications, extra
    /// barcodes, price lines, suppliers) must never bleed into or be
    /// rewritten by another material's save. The window-level mapping
    /// (Collect/BindToUi) is covered by the field table in
    /// docs/MATCARD_FIXES.md; these facts prove the repository half.
    /// Execution is owed on Windows (net8.0-windows testhost), as with the
    /// rest of Kern.Tests.
    /// </summary>
    public class MaterialCardRoundTripTests
    {
        private const string Company = "TEST-ROUNDTRIP";

        [Fact]
        public void SaveLoad_RoundTripsEveryCardField()
        {
            using var ctx = CreateContext();
            var saved = MatRepo.Save(FullyPopulated("RT-001"), isNew: true);
            Assert.True(saved.Id > 0);

            var m = MatRepo.LoadById(saved.Id);
            Assert.NotNull(m);

            Assert.Equal("RT-001", m!.Code);
            Assert.Equal("Round trip material", m.Name);
            using (var verify = new SqliteConnection($"Data Source={ctx.DatabasePath};"))
            {
                verify.Open();
                using var command = verify.CreateCommand();
                command.CommandText = "SELECT name FROM materials WHERE material_id = $id";
                command.Parameters.AddWithValue("$id", saved.Id);
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("Round trip material", reader.GetString(0));
                using var schema = verify.CreateCommand();
                schema.CommandText = "PRAGMA table_info(materials)";
                using var schemaReader = schema.ExecuteReader();
                var names = new List<string>();
                while (schemaReader.Read()) names.Add(schemaReader.GetString(1));
                Assert.Contains("name", names);
                Assert.DoesNotContain("name_ar", names);
                Assert.DoesNotContain("name_en", names);
                using var translationTable = verify.CreateCommand();
                translationTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='material_translations'";
                Assert.Equal(0L, Convert.ToInt64(translationTable.ExecuteScalar()));
            }
            Assert.Equal("GRP-1", m.GroupCode);
            Assert.Equal("Inactive", m.Status);
            Assert.Equal("Kit", m.ItemType);
            Assert.Equal("CAT-9", m.Category);
            Assert.Equal("BrandX", m.Brand);
            Assert.Equal("ModelY", m.Model);
            Assert.Equal("Purple", m.Color);
            Assert.Equal("XL", m.SizeSpec);
            Assert.Equal("MakerZ", m.Manufacturer);
            Assert.Equal("SY", m.OriginCountry);
            Assert.Equal("Local", m.Source);
            Assert.Equal("ALT-1", m.AltCode);
            Assert.Equal("6221234567890", m.Gtin);
            Assert.Equal("SUP-ITEM-1", m.SupplierCode);
            Assert.Equal("B", m.AbcClass);
            Assert.Equal("Some notes", m.Notes);

            Assert.False(m.IsStocked);
            Assert.True(m.IsPurchased);
            Assert.True(m.IsSold);
            Assert.True(m.IsProduced);
            Assert.True(m.IsService);

            Assert.Equal(5m, m.MinQty);
            Assert.Equal(500m, m.MaxQty);
            Assert.Equal("EUR", m.PriceCurrency);
            Assert.Equal(42.5m, m.Cost);
            Assert.Equal("FIFO", m.CostMethod);
            Assert.Equal(11m, m.TaxRate);
            Assert.True(m.IsTaxable);
            Assert.True(m.PriceIncludesTax);
            Assert.True(m.HasSerial);
            Assert.True(m.HasExpiry);
            Assert.True(m.HasBatch);
            Assert.Equal(180, m.ShelfLifeDays);

            Assert.Equal("kg", m.Unit1);
            Assert.Equal(1m, m.Unit1Factor);
            Assert.Equal("BC-1", m.Unit1Barcode);
            Assert.Equal("g250", m.Unit2);
            Assert.Equal(0.25m, m.Unit2Factor);
            Assert.Equal("BC-2", m.Unit2Barcode);
            Assert.Equal("kg", m.DefaultUnit);
            Assert.Equal(100m, m.PriceList);
            Assert.Equal(110m, m.PriceRetail);
            Assert.Equal(90m, m.PriceWholesale);
            Assert.Equal(95m, m.PriceSpecial);
            Assert.Equal(120m, m.PriceExport);
            Assert.Equal(85m, m.PriceDistributor);
            Assert.Equal(7.5m, m.PriceMin);
            Assert.Contains("g250", m.UnitsJson);

            Assert.Equal(33m, m.QtyOnHand);
            Assert.Equal(7m, m.ReorderQty);
            Assert.Equal(3m, m.SafetyStock);
            Assert.Equal("2", m.DefaultWarehouse);
            Assert.Equal("A-01", m.ShelfBin);
            Assert.Equal(14, m.LeadTimeDays);
            Assert.Equal(1.25m, m.WeightNet);
            Assert.Equal(1.5m, m.WeightGross);
            Assert.True(m.AllowNegative);
            Assert.True(m.TrackLocation);
            Assert.True(m.QualityCheckRequired);

            Assert.Equal("ACC-INV", m.AccInventory);
            Assert.Equal("ACC-SAL", m.AccSales);
            Assert.Equal("ACC-SRT", m.AccSalesReturn);
            Assert.Equal("ACC-COGS", m.AccCogs);
            Assert.Equal("ACC-PUR", m.AccPurchase);
            Assert.Equal("ACC-PRT", m.AccPurchaseReturn);
            Assert.Equal("ACC-EXP", m.AccExpense);
            Assert.Equal("ACC-WIP", m.AccWip);

            Assert.False(m.AllowSale);
            Assert.True(m.AllowPurchase);
            Assert.False(m.AllowReturn);
            Assert.True(m.SalesBlocked);

            Assert.Equal("img/rt-001.png", m.ImagePath);

            Assert.Equal(2, m.Attributes.Count);
            Assert.Contains(m.Attributes, a => a.Key == "ColorTemp" && a.Value == "3000K" && a.Group == "Electrical" && a.SortOrder == 1);
            Assert.Contains(m.Attributes, a => a.Key == "Voltage" && a.Value == "220V" && a.Group == "Electrical" && a.SortOrder == 2);
            Assert.Single(m.ExtraBarcodes);
            Assert.Equal("EXTRA-1", m.ExtraBarcodes[0].Barcode);
            Assert.Single(m.PriceLines);
            Assert.Equal(77m, m.PriceLines[0].Price);
            Assert.Single(m.Suppliers);
            Assert.Equal("SUP-1", m.Suppliers[0].SupplierCode);
        }

        [Fact]
        public void NextCode_UsesDocumentSequenceSchema()
        {
            using var ctx = CreateContext();
            MatRepo.Save(FullyPopulated("MAT-0007"), isNew: true);

            Assert.Equal("MAT-0008", MatRepo.NextCode("MAT-"));
        }

        [Fact]
        public void Update_ReplacesOwnChildren_AndNeverTouchesAnotherMaterial()
        {
            using var ctx = CreateContext();
            var a = MatRepo.Save(FullyPopulated("RT-A"), isNew: true);
            var bRecord = FullyPopulated("RT-B");
            bRecord.ExtraBarcodes[0].Barcode = "EXTRA-2";
            bRecord.Attributes = new List<MatAttrRow>
            {
                new() { Key = "BSpec", Value = "BValue", Group = "BG", SortOrder = 1 }
            };
            var b = MatRepo.Save(bRecord, isNew: true);

            // Rewrite A completely: new name, one different attribute.
            var aUpdated = MatRepo.LoadById(a.Id)!;
            aUpdated.Name = "A renamed";
            aUpdated.Attributes = new List<MatAttrRow>
            {
                new() { Key = "NewSpec", Value = "NewValue", Group = "NG", SortOrder = 1 }
            };
            MatRepo.Save(aUpdated, isNew: false);

            var aReloaded = MatRepo.LoadById(a.Id)!;
            Assert.Equal("A renamed", aReloaded.Name);
            Assert.Single(aReloaded.Attributes);
            Assert.Equal("NewSpec", aReloaded.Attributes[0].Key);

            // B is untouched: name, its own single attribute, its units.
            var bReloaded = MatRepo.LoadById(b.Id)!;
            Assert.Equal("Round trip material", bReloaded.Name);
            Assert.Single(bReloaded.Attributes);
            Assert.Equal("BSpec", bReloaded.Attributes[0].Key);
            Assert.Equal("g250", bReloaded.Unit2);
            Assert.Contains("g250", bReloaded.UnitsJson);
        }

        private static MatRepo.MatRec FullyPopulated(string code) => new()
        {
            Code = code,
            Name = "Round trip material",
            GroupCode = "GRP-1",
            Status = "Inactive",
            ItemType = "Kit",
            Category = "CAT-9",
            Brand = "BrandX",
            Model = "ModelY",
            Color = "Purple",
            SizeSpec = "XL",
            Manufacturer = "MakerZ",
            OriginCountry = "SY",
            Source = "Local",
            AltCode = "ALT-1",
            Gtin = "6221234567890",
            SupplierCode = "SUP-ITEM-1",
            AbcClass = "B",
            Notes = "Some notes",
            IsStocked = false,
            IsPurchased = true,
            IsSold = true,
            IsProduced = true,
            IsService = true,
            MinQty = 5m,
            MaxQty = 500m,
            PriceCurrency = "EUR",
            Cost = 42.5m,
            CostMethod = "FIFO",
            TaxRate = 11m,
            IsTaxable = true,
            PriceIncludesTax = true,
            HasSerial = true,
            HasExpiry = true,
            HasBatch = true,
            ShelfLifeDays = 180,
            Unit1 = "kg",
            Unit1Factor = 1m,
            Unit1Barcode = "BC-1",
            Unit2 = "g250",
            Unit2Factor = 0.25m,
            Unit2Barcode = "BC-2",
            DefaultUnit = "kg",
            PriceList = 100m,
            PriceRetail = 110m,
            PriceWholesale = 90m,
            PriceSpecial = 95m,
            PriceExport = 120m,
            PriceDistributor = 85m,
            PriceMin = 7.5m,
            UnitsJson = "[{\"UnitName\":\"kg\",\"Factor\":1},{\"UnitName\":\"g250\",\"Factor\":0.25}]",
            QtyOnHand = 33m,
            ReorderQty = 7m,
            SafetyStock = 3m,
            DefaultWarehouse = "2",
            ShelfBin = "A-01",
            LeadTimeDays = 14,
            WeightNet = 1.25m,
            WeightGross = 1.5m,
            AllowNegative = true,
            TrackLocation = true,
            QualityCheckRequired = true,
            AccInventory = "ACC-INV",
            AccSales = "ACC-SAL",
            AccSalesReturn = "ACC-SRT",
            AccCogs = "ACC-COGS",
            AccPurchase = "ACC-PUR",
            AccPurchaseReturn = "ACC-PRT",
            AccExpense = "ACC-EXP",
            AccWip = "ACC-WIP",
            AllowSale = false,
            AllowPurchase = true,
            AllowReturn = false,
            SalesBlocked = true,
            ImagePath = "img/rt-001.png",
            Attributes = new List<MatAttrRow>
            {
                new() { Key = "ColorTemp", Value = "3000K", Group = "Electrical", SortOrder = 1 },
                new() { Key = "Voltage", Value = "220V", Group = "Electrical", SortOrder = 2 }
            },
            ExtraBarcodes = new List<MatRepo.MatBarcode>
            {
                new() { Barcode = "EXTRA-1", BarcodeType = "EAN", UnitName = "kg" }
            },
            PriceLines = new List<MatRepo.MatPriceLine>
            {
                new() { PriceLevel = "VIP", Price = 77m, Currency = "EUR", UnitName = "kg" }
            },
            Suppliers = new List<MatRepo.MatSupplier>
            {
                new() { SupplierCode = "SUP-1", SupplierItemCode = "SUP-ITEM-1", LeadTimeDays = 9, MinOrderQty = 12m, IsPreferred = true }
            }
        };

        // ----------------------------------------------------------------
        //  Context: temp SQLite company DB wired into the app runtime,
        //  mirroring the setup pattern of the other repository facts.
        // ----------------------------------------------------------------
        private sealed class RoundTripContext : IDisposable
        {
            private readonly string _databasePath;
            private readonly FieldInfo _settingsField;
            private readonly object? _oldSettings;
            private readonly string? _oldCompany;

            public string DatabasePath => _databasePath;

            public RoundTripContext(string databasePath, FieldInfo settingsField, object? oldSettings, string? oldCompany)
            {
                _databasePath = databasePath;
                _settingsField = settingsField;
                _oldSettings = oldSettings;
                _oldCompany = oldCompany;
            }

            public void Dispose()
            {
                _settingsField.SetValue(null, _oldSettings);
                AppState.CurrentCompanyCode = _oldCompany;
                SqliteConnection.ClearAllPools();
                if (File.Exists(_databasePath)) File.Delete(_databasePath);
            }
        }

        private static RoundTripContext CreateContext()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-roundtrip-{Guid.NewGuid():N}.sqlite3");
            var settingsField = typeof(AppCfgSvc).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldSettings = settingsField.GetValue(null);
            var oldCompany = AppState.CurrentCompanyCode;
            try
            {
                settingsField.SetValue(null, new AppSetng
                {
                    Database = new DatabaseSettings { DefaultProvider = "Sqlite" },
                    ConnectionStrings = new Dictionary<string, ConnectionStringSettings>
                    {
                        ["Default"] = new()
                        {
                            Provider = "Sqlite",
                            ConnectionString = $"Data Source={databasePath};"
                        }
                    }
                });
                AppState.CurrentCompanyCode = Company;

                ApplySqliteMigrations(databasePath);
                typeof(MatRepo).Assembly
                    .GetType("ShouTech.App.Services.MatRepo")!
                    .GetMethod("EnsureSchema", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, null);

                return new RoundTripContext(databasePath, settingsField, oldSettings, oldCompany);
            }
            catch
            {
                settingsField.SetValue(null, oldSettings);
                AppState.CurrentCompanyCode = oldCompany;
                throw;
            }
        }

        private static void ApplySqliteMigrations(string databasePath)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "dba", "mig")))
                root = root.Parent;

            Assert.NotNull(root);
            var migrator = Path.Combine(root!.FullName, "tools", "SqlMigrator", "bin", "Release", "net8.0", "SqlMigrator.dll");
            Assert.True(File.Exists(migrator), $"SQL migrator was not built: {migrator}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = root.FullName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add(migrator);
            process.StartInfo.ArgumentList.Add("--provider");
            process.StartInfo.ArgumentList.Add("sqlite");
            process.StartInfo.ArgumentList.Add("--connection");
            process.StartInfo.ArgumentList.Add($"Data Source={databasePath};");

            Assert.True(process.Start(), "Could not start the DBA SQL migrator.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "DBA SQL migrations timed out.");
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0,
                $"Applying SQLite DBA migrations failed (exit {process.ExitCode}).\n{output}\n{errors}");
        }
    }
}
