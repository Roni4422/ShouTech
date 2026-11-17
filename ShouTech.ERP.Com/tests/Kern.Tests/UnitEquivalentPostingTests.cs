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
    /// Unit-equivalent posting facts (owner definition, 2026-11-17): stock is
    /// kept in the material's first (base) unit; a document line entered in
    /// another unit moves qty x equivalent(that unit). The material below is
    /// stocked in kg (base, equivalent 1) with a second unit "g250" whose
    /// equivalent is 0.25 — selling 4 x g250 must deduct exactly 1 kg.
    /// Execution is owed on Windows (net8.0-windows testhost), as with the
    /// rest of Kern.Tests.
    /// </summary>
    public class UnitEquivalentPostingTests
    {
        private const string Company = "TEST-EQUIV";

        [Fact]
        public void SalesInvoice_SecondUnit_DeductsQuantityTimesEquivalent()
        {
            using var ctx = CreateContext(unitsJson: UnitsJson, balanceQty: 10m, balanceAvg: 8m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.SalesInvoice,
                DocNo = "S-EQ-1",
                DocDate = new DateTime(2026, 1, 5),
                Lines =
                {
                    new InvLine { MaterialId = 1, MaterialCode = "MAT-EQ", Unit = "g250", Qty = 4m, UnitPrice = 2.5m }
                }
            });

            // 4 x 0.25 = exactly 1 kg out; unit cost restated per base unit (2.5 / 0.25 = 10).
            Assert.Equal(1m, ctx.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE document_no='S-EQ-1'"));
            Assert.Equal(10m, ctx.ScalarDecimal("SELECT unit_cost FROM material_stock_movements WHERE document_no='S-EQ-1'"));
            Assert.Equal(9m, ctx.BalanceQty());
        }

        [Fact]
        public void PurchaseInvoice_SecondUnit_AddsQuantityTimesEquivalent()
        {
            using var ctx = CreateContext(unitsJson: UnitsJson, balanceQty: 10m, balanceAvg: 8m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.PurchaseInvoice,
                DocNo = "P-EQ-1",
                DocDate = new DateTime(2026, 1, 5),
                Lines =
                {
                    new InvLine { MaterialId = 1, MaterialCode = "MAT-EQ", Unit = "g250", Qty = 4m, UnitPrice = 2.5m }
                }
            });

            Assert.Equal(1m, ctx.ScalarDecimal("SELECT qty_in FROM material_stock_movements WHERE document_no='P-EQ-1'"));
            Assert.Equal(11m, ctx.BalanceQty());
        }

        [Fact]
        public void SalesInvoice_BaseUnit_PostsRawQuantity()
        {
            using var ctx = CreateContext(unitsJson: UnitsJson, balanceQty: 10m, balanceAvg: 8m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.SalesInvoice,
                DocNo = "S-EQ-2",
                DocDate = new DateTime(2026, 1, 5),
                Lines =
                {
                    new InvLine { MaterialId = 1, MaterialCode = "MAT-EQ", Unit = "kg", Qty = 3m, UnitPrice = 10m }
                }
            });

            Assert.Equal(3m, ctx.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE document_no='S-EQ-2'"));
            Assert.Equal(7m, ctx.BalanceQty());
        }

        [Fact]
        public void SalesInvoice_UnknownUnit_PostsRawQuantity()
        {
            using var ctx = CreateContext(unitsJson: UnitsJson, balanceQty: 10m, balanceAvg: 8m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.SalesInvoice,
                DocNo = "S-EQ-3",
                DocDate = new DateTime(2026, 1, 5),
                Lines =
                {
                    new InvLine { MaterialId = 1, MaterialCode = "MAT-EQ", Unit = "crate", Qty = 2m, UnitPrice = 10m }
                }
            });

            // A unit that is not on the material card cannot be converted:
            // the quantity is treated as already expressed in base units.
            Assert.Equal(2m, ctx.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE document_no='S-EQ-3'"));
            Assert.Equal(8m, ctx.BalanceQty());
        }

        [Fact]
        public void SalesInvoice_SecondUnitFromLegacyColumns_Converts()
        {
            // Legacy rows without units_json resolve through unit1..unit5.
            using var ctx = CreateContext(unitsJson: "[]", balanceQty: 10m, balanceAvg: 8m);
            ctx.Post(new InvDoc
            {
                Kind = InvDocKind.SalesInvoice,
                DocNo = "S-EQ-4",
                DocDate = new DateTime(2026, 1, 5),
                Lines =
                {
                    new InvLine { MaterialId = 1, MaterialCode = "MAT-EQ", Unit = "g250", Qty = 4m, UnitPrice = 2.5m }
                }
            });

            Assert.Equal(1m, ctx.ScalarDecimal("SELECT qty_out FROM material_stock_movements WHERE document_no='S-EQ-4'"));
            Assert.Equal(9m, ctx.BalanceQty());
        }

        private const string UnitsJson =
            "[{\"UnitName\":\"kg\",\"Factor\":1},{\"UnitName\":\"g250\",\"Factor\":0.25}]";

        // ----------------------------------------------------------------
        //  Context: temp SQLite company DB wired into the app runtime,
        //  mirroring the setup pattern of the MoveMaterialHistory fact.
        // ----------------------------------------------------------------
        private sealed class PostingContext : IDisposable
        {
            private readonly string _databasePath;
            private readonly FieldInfo _settingsField;
            private readonly object? _oldSettings;
            private readonly string? _oldCompany;

            public PostingContext(string databasePath, FieldInfo settingsField, object? oldSettings, string? oldCompany)
            {
                _databasePath = databasePath;
                _settingsField = settingsField;
                _oldSettings = oldSettings;
                _oldCompany = oldCompany;
            }

            public void Post(InvDoc doc)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var tx = connection.BeginTransaction();
                StkPost.ApplyDocument(connection, tx, doc);
                tx.Commit();
            }

            public decimal ScalarDecimal(string sql)
            {
                using var connection = new SqliteConnection($"Data Source={_databasePath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                var value = cmd.ExecuteScalar();
                return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            public decimal BalanceQty() =>
                ScalarDecimal("SELECT available_qty FROM material_stock_balances WHERE material_id=1");

            public void Dispose()
            {
                _settingsField.SetValue(null, _oldSettings);
                AppState.CurrentCompanyCode = _oldCompany;
                SqliteConnection.ClearAllPools();
                if (File.Exists(_databasePath)) File.Delete(_databasePath);
            }
        }

        private static PostingContext CreateContext(string unitsJson, decimal balanceQty, decimal balanceAvg)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-equiv-{Guid.NewGuid():N}.sqlite3");
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

                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO materials
                          (company_id, code, name_ar, name_en, is_stocked, is_service, allow_negative,
                           unit1, unit1_factor, unit2, unit2_factor, units_json)
                        VALUES
                          ('TEST-EQUIV', 'MAT-EQ', 'Equivalent test', 'Equivalent test', 1, 0, 0,
                           'kg', 1, 'g250', 0.25, $json);
                        INSERT INTO material_stock_balances
                          (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                           average_cost, last_cost, is_deleted)
                        VALUES
                          ('TEST-EQUIV', 1, 1, $qty, 0, $avg, $avg, 0);
                        """;
                    var jsonParam = cmd.CreateParameter();
                    jsonParam.ParameterName = "$json";
                    jsonParam.Value = unitsJson;
                    cmd.Parameters.Add(jsonParam);
                    var qtyParam = cmd.CreateParameter();
                    qtyParam.ParameterName = "$qty";
                    qtyParam.Value = balanceQty;
                    cmd.Parameters.Add(qtyParam);
                    var avgParam = cmd.CreateParameter();
                    avgParam.ParameterName = "$avg";
                    avgParam.Value = balanceAvg;
                    cmd.Parameters.Add(avgParam);
                    cmd.ExecuteNonQuery();
                }

                return new PostingContext(databasePath, settingsField, oldSettings, oldCompany);
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
