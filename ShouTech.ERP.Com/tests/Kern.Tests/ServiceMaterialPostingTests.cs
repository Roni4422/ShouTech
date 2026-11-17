using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.App.Views;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Service-material posting facts (owner rule, 2026-11-17): a service
    /// material is non-stock — it can be sold and purchased (the document
    /// posts; the accounting side lives in InvRepo and is unaffected), but
    /// its lines must create NO stock movement and NO balance change.
    /// Service-ness is honored from any of the three views the material
    /// card keeps in agreement: the Service type, the service flag, or
    /// the not-stocked flag. Execution is owed on Windows
    /// (net8.0-windows testhost), as with the rest of Kern.Tests.
    /// </summary>
    public class ServiceMaterialPostingTests
    {
        private const string Company = "TEST-SERVICE";

        [Fact]
        public void SalesInvoice_ServiceByFlags_CreatesNoMovementOrBalanceChange()
        {
            using var ctx = CreateContext();
            ctx.SeedMaterial(1, "SVC-1", isStocked: true, isService: true, itemType: "Service", balanceQty: 10m);
            ctx.Post(SalesDoc("S-SVC-1", Line(1, "SVC-1", 5m)));

            Assert.Equal(0, ctx.MovementCount());
            Assert.Equal(10m, ctx.BalanceQty(1));
        }

        [Fact]
        public void SalesInvoice_ServiceByTypeOnly_CreatesNoMovement()
        {
            // Legacy/imported row: type says Service, flags were never synced.
            using var ctx = CreateContext();
            ctx.SeedMaterial(1, "SVC-2", isStocked: true, isService: false, itemType: "Service", balanceQty: 10m);
            ctx.Post(SalesDoc("S-SVC-2", Line(1, "SVC-2", 5m)));

            Assert.Equal(0, ctx.MovementCount());
            Assert.Equal(10m, ctx.BalanceQty(1));
        }

        [Fact]
        public void PurchaseInvoice_Service_CreatesNoStockIn()
        {
            using var ctx = CreateContext();
            ctx.SeedMaterial(1, "SVC-3", isStocked: false, isService: true, itemType: "Service", balanceQty: 0m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.PurchaseInvoice,
                DocNo = "P-SVC-3",
                DocDate = new DateTime(2026, 1, 5),
                Lines = { Line(1, "SVC-3", 5m) }
            });

            Assert.Equal(0, ctx.MovementCount());
        }

        [Fact]
        public void SalesInvoice_MixedDoc_StockLineMovesServiceLineDoesNot()
        {
            using var ctx = CreateContext();
            ctx.SeedMaterial(1, "STK-1", isStocked: true, isService: false, itemType: "Stock", balanceQty: 10m);
            ctx.SeedMaterial(2, "SVC-4", isStocked: true, isService: true, itemType: "Service", balanceQty: 0m);
            ctx.Post(SalesDoc("S-MIX-1", Line(1, "STK-1", 3m), Line(2, "SVC-4", 7m)));

            Assert.Equal(1, ctx.MovementCount());
            Assert.Equal(7m, ctx.BalanceQty(1));
        }

        [Fact]
        public void PurchaseInvoice_NotStocked_CreatesNoStockIn()
        {
            using var ctx = CreateContext();
            ctx.SeedMaterial(1, "NST-1", isStocked: false, isService: false, itemType: "NonStock", balanceQty: 0m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.PurchaseInvoice,
                DocNo = "P-NST-1",
                DocDate = new DateTime(2026, 1, 5),
                Lines = { Line(1, "NST-1", 5m) }
            });

            Assert.Equal(0, ctx.MovementCount());
        }

        private static InvLine Line(long materialId, string code, decimal qty) => new()
        {
            MaterialId = materialId,
            MaterialCode = code,
            Unit = "pcs",
            Qty = qty,
            UnitPrice = 10m
        };

        private static InvDoc SalesDoc(string docNo, params InvLine[] lines) => new()
        {
            Kind = InvDocKind.SalesInvoice,
            DocNo = docNo,
            DocDate = new DateTime(2026, 1, 5),
            Lines = new List<InvLine>(lines)
        };

        // ----------------------------------------------------------------
        //  Context: temp SQLite company DB wired into the app runtime,
        //  mirroring the setup pattern of the other posting facts.
        // ----------------------------------------------------------------
        private sealed class ServiceContext : IDisposable
        {
            private readonly string _databasePath;
            private readonly FieldInfo _settingsField;
            private readonly object? _oldSettings;
            private readonly string? _oldCompany;

            public ServiceContext(string databasePath, FieldInfo settingsField, object? oldSettings, string? oldCompany)
            {
                _databasePath = databasePath;
                _settingsField = settingsField;
                _oldSettings = oldSettings;
                _oldCompany = oldCompany;
            }

            public void SeedMaterial(long id, string code, bool isStocked, bool isService, string itemType, decimal balanceQty)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO materials
                      (material_id, company_id, code, name_ar, name_en, is_stocked, is_service,
                       allow_negative, item_type, unit1, unit1_factor)
                    VALUES
                      ($id, 'TEST-SERVICE', $code, 'Service test', 'Service test', $stocked, $service,
                       0, $type, 'pcs', 1);
                    INSERT INTO material_stock_balances
                      (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                       average_cost, last_cost, is_deleted)
                    VALUES
                      ('TEST-SERVICE', 1, $id, $qty, 0, 8, 8, 0);
                    """;
                void Add(string name, object value)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = name;
                    p.Value = value;
                    cmd.Parameters.Add(p);
                }
                Add("$id", id);
                Add("$code", code);
                Add("$stocked", isStocked ? 1 : 0);
                Add("$service", isService ? 1 : 0);
                Add("$type", itemType);
                Add("$qty", balanceQty);
                cmd.ExecuteNonQuery();
            }

            public void Post(InvDoc doc)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var tx = connection.BeginTransaction();
                StkPost.ApplyDocument(connection, tx, doc);
                tx.Commit();
            }

            public int MovementCount()
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM material_stock_movements";
                return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            public decimal BalanceQty(long materialId)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT available_qty FROM material_stock_balances WHERE material_id=$id";
                var p = cmd.CreateParameter();
                p.ParameterName = "$id";
                p.Value = materialId;
                cmd.Parameters.Add(p);
                return Convert.ToDecimal(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            public void Dispose()
            {
                _settingsField.SetValue(null, _oldSettings);
                AppState.CurrentCompanyCode = _oldCompany;
                SqliteConnection.ClearAllPools();
                if (File.Exists(_databasePath)) File.Delete(_databasePath);
            }
        }

        private static ServiceContext CreateContext()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-service-{Guid.NewGuid():N}.sqlite3");
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
                typeof(StkPost).Assembly
                    .GetType("ShouTech.App.Services.MatRepo")!
                    .GetMethod("EnsureSchema", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, null);

                return new ServiceContext(databasePath, settingsField, oldSettings, oldCompany);
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
