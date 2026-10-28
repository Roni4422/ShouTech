using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Xml.Linq;
using System.Windows;
using ShouTech.App.Converters;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.App;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Application.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kern.Tests
{
    public class UnitTest1
    {
        [Fact]
        public async Task SchemaMigrator_ShouldApplyConditionalJournalColumnsToFreshDatabase()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"shoutech-migration-{Guid.NewGuid():N}.db");

            try
            {
                var result = await SchemaMigrator.ApplyAllAsync(
                    "sqlite",
                    $"Data Source={databasePath};");

                Assert.True(result.Success, result.Message);

                await using var connection = new SqliteConnection($"Data Source={databasePath};");
                await connection.OpenAsync();
                foreach (var column in new[] { "reference_number", "currency_code", "exchange_rate" })
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT COUNT(*) FROM pragma_table_info('journal_entries') WHERE name = @column;";
                    command.Parameters.AddWithValue("@column", column);
                    Assert.Equal(1L, await command.ExecuteScalarAsync());
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
        }

        [Fact]
        public async Task CoCreateSvc_ShouldSaveCompanyUsingWizardDatabasePath()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"shoutech-company-create-{Guid.NewGuid():N}.db");

            try
            {
                var migration = await SchemaMigrator.ApplyAllAsync(
                    "sqlite",
                    $"Data Source={databasePath};");
                Assert.True(migration.Success, migration.Message);

                var record = new StoredCoRec
                {
                    CompanyId = Guid.NewGuid(),
                    Code = "TEST-CREATE",
                    Name = "Test Company",
                    NameEn = "Test Company EN",
                    CurrencyCode = "SYP",
                    LanguageCode = "ar-SY",
                    FiscalYearStart = new DateTime(2026, 1, 1),
                    FiscalYearEnd = new DateTime(2026, 12, 31),
                    AdminUsername = "admin",
                    AdminPasswordHash = "test-hash",
                    AdminPasswordSalt = "test-salt",
                    AdminFullName = "Test Admin"
                };
                var request = new NewCoReq
                {
                    DatabaseProvider = "SQLite",
                    DbPath = databasePath
                };
                var saveMethod = typeof(CoCreateSvc).GetMethod(
                    "TrySaveToDatabaseAsync",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(saveMethod);

                var saveTask = (Task<bool>)saveMethod!.Invoke(
                    null,
                    new object[] { record, request })!;
                Assert.True(await saveTask);

                await using var connection = new SqliteConnection($"Data Source={databasePath};");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM companies WHERE company_code = @code;";
                command.Parameters.AddWithValue("@code", record.Code);
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
        }

        [Fact]
        public async Task CoRegSvc_ShouldUpdateCompanyUsingStoredDatabasePath()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"shoutech-company-update-{Guid.NewGuid():N}.db");

            try
            {
                var migration = await SchemaMigrator.ApplyAllAsync(
                    "sqlite",
                    $"Data Source={databasePath};");
                Assert.True(migration.Success, migration.Message);

                await using (var connection = new SqliteConnection($"Data Source={databasePath};"))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO companies
                            (company_id, company_code, company_name_ar, company_name_en,
                             currency_code, language_code)
                        VALUES
                            ('test-id', 'TEST-UPDATE', 'Before', 'Before EN', 'SYP', 'ar-SY');
                        """;
                    await command.ExecuteNonQueryAsync();
                }

                var profile = new StoredCoRec
                {
                    Code = "TEST-UPDATE",
                    Name = "After",
                    NameEn = "After EN",
                    CurrencyCode = "USD",
                    LanguageCode = "en",
                    DatabaseProvider = "SQLite",
                    DbPath = databasePath
                };
                var updateMethod = typeof(CoRegSvc).GetMethod(
                    "TryUpdateInDatabaseAsync",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(updateMethod);

                var updateTask = (Task<bool>)updateMethod!.Invoke(null, new object[] { profile })!;
                Assert.True(await updateTask);

                await using var verify = new SqliteConnection($"Data Source={databasePath};");
                await verify.OpenAsync();
                await using var verifyCommand = verify.CreateCommand();
                verifyCommand.CommandText = """
                    SELECT company_name_ar, company_name_en, currency_code, language_code
                    FROM companies
                    WHERE company_code = 'TEST-UPDATE';
                    """;
                await using var reader = await verifyCommand.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("After", reader.GetString(0));
                Assert.Equal("After EN", reader.GetString(1));
                Assert.Equal("USD", reader.GetString(2));
                Assert.Equal("en", reader.GetString(3));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
        }

        [Fact]
        public async Task CoRegSvc_ShouldInsertMissingCompanyRowWhenEditingLegacyLocalEntry()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"shoutech-company-register-{Guid.NewGuid():N}.db");

            try
            {
                var migration = await SchemaMigrator.ApplyAllAsync(
                    "sqlite",
                    $"Data Source={databasePath};");
                Assert.True(migration.Success, migration.Message);

                var companyId = Guid.NewGuid();
                var profile = new StoredCoRec
                {
                    CompanyId = companyId,
                    Code = "LEGACY-LOCAL",
                    Name = "Legacy Company",
                    NameEn = "Legacy Company EN",
                    TaxNumber = "TAX-123",
                    License = "LIC-456",
                    CurrencyCode = "SYP",
                    LanguageCode = "ar-SY",
                    DatabaseProvider = "SQLite",
                    DbPath = databasePath
                };
                var updateMethod = typeof(CoRegSvc).GetMethod(
                    "TryUpdateInDatabaseAsync",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(updateMethod);

                var updateTask = (Task<bool>)updateMethod!.Invoke(null, new object[] { profile })!;
                Assert.True(await updateTask);

                await using var connection = new SqliteConnection($"Data Source={databasePath};");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT company_id, company_name_ar, company_name_en,
                           commercial_registration, currency_code, language_code
                    FROM companies
                    WHERE company_code = @code;
                    """;
                command.Parameters.AddWithValue("@code", profile.Code);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(companyId.ToString(), reader.GetString(0));
                Assert.Equal(profile.Name, reader.GetString(1));
                Assert.Equal(profile.NameEn, reader.GetString(2));
                Assert.Equal(profile.TaxNumber, reader.GetString(3));
                Assert.Equal(profile.CurrencyCode, reader.GetString(4));
                Assert.Equal(profile.LanguageCode, reader.GetString(5));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
        }

        [Fact]
        public async Task RecycleBinSvc_ShouldDetectBooleanAndIntegerSoftDeleteColumns()
        {
            var settingsField = typeof(AppCfgSvc).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldSettings = settingsField.GetValue(null);
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
                            ConnectionString = "Data Source=:memory:"
                        }
                    }
                });

                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                using (var create = connection.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE flag_types (bool_flag BOOLEAN, int_flag INTEGER);";
                    create.ExecuteNonQuery();
                }

                var detector = typeof(RecycleBinSvc).GetMethod(
                    "IsBooleanColumnAsync",
                    BindingFlags.NonPublic | BindingFlags.Static)!;

                var booleanTask = (Task<bool>)detector.Invoke(
                    null, new object[] { connection, "flag_types", "bool_flag", CancellationToken.None })!;
                Assert.True(await booleanTask);

                var integerTask = (Task<bool>)detector.Invoke(
                    null, new object[] { connection, "flag_types", "int_flag", CancellationToken.None })!;
                Assert.False(await integerTask);
            }
            finally
            {
                settingsField.SetValue(null, oldSettings);
            }
        }

        [Fact]
        public void NullToVisibilityConverter_WhenConvertBackIsCalled_ShouldReturnFalseWithoutThrowing()
        {
            var converter = new NullToVisibilityConverter();

            var result = converter.ConvertBack(null!, typeof(bool), null!, null!);

            Assert.False((bool)result);
        }

        [Theory]
        [InlineData("materials", "materials", "products", "Products")]
        [InlineData("warehouses", "Warehouses", "warehouses")]
        [InlineData("accounts", "accounts", "chart_of_accounts", "Accounts")]
        public void DataExportSvc_ShouldResolveActualDatabaseTableName(
            string expected,
            params string[] candidates)
        {
            var resolve = typeof(DataExportSvc).GetMethod(
                "ResolveTableName",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            var result = resolve.Invoke(null, new object[]
            {
                new[] { "materials", "warehouses", "accounts" },
                candidates
            });

            Assert.Equal(expected, result);
        }

        [Fact]
        public void DataExportSvc_ShouldExportOnlyMaterialCardFields()
        {
            var getFields = typeof(DataExportSvc).GetMethod(
                "GetMaterialCardExportFieldNames",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var availableFields = new[]
            {
                "material_id", "company_id", "code", "name_ar", "name_en", "units_json",
                "prices_json", "attributes_json", "created_at", "created_by", "qty_on_hand"
            };

            var result = (IReadOnlyList<string>)getFields.Invoke(
                null, new object[] { "materials", availableFields })!;

            Assert.Equal(new[] { "material_id", "code", "name_ar", "name_en", "qty_on_hand" }, result);
        }

        [Fact]
        public void PwdHash_ShouldVerifyOnlyTheOriginalPassword()
        {
            var password = "test-" + Guid.NewGuid().ToString("N") + "!";
            var stored = PwdHash.Create(password, iterations: 100_000);

            Assert.StartsWith("v1$PBKDF2-SHA512$100000$", stored.Hash);
            Assert.True(PwdHash.Verify(password, stored.Hash, stored.Salt));
            Assert.False(PwdHash.Verify("wrong password", stored.Hash, stored.Salt));
            Assert.False(PwdHash.NeedsRehash(stored.Hash, desiredIterations: 100_000));
        }

        [Fact]
        public void XlsxWriter_ShouldStoreDatasetIdentityInAHiddenHeaderColumn()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-{Guid.NewGuid():N}.xlsx");
            try
            {
                XlsxWrt.Write(
                    path,
                    "Customers",
                    new[] { new XlsxWrt.Col { Header = "Customer code" } },
                    Array.Empty<IReadOnlyList<object?>>(),
                    rtl: true,
                    hiddenHeader: "__SHOUTECH_DATASET_V1__:encrypted-marker");

                using var archive = ZipFile.OpenRead(path);
                using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var worksheet = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var hiddenColumn = worksheet.Root!.Element(ns + "cols")!.Elements(ns + "col").Last();
                Assert.Contains((string?)hiddenColumn.Attribute("hidden"), new[] { "1", "true" });

                var headers = worksheet.Root.Element(ns + "sheetData")!.Elements(ns + "row").First().Elements(ns + "c").ToArray();
                Assert.Equal(2, headers.Length);
                Assert.Equal("B1", (string?)headers[1].Attribute("r"));
                Assert.Equal("__SHOUTECH_DATASET_V1__:encrypted-marker",
                    headers[1].Element(ns + "is")!.Element(ns + "t")!.Value);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void XlsxWriter_ShouldWriteIdentifiersAndOffsetDatesAsTextCells()
        {
            var path = Path.Combine(Path.GetTempPath(), $"shoutech-{Guid.NewGuid():N}.xlsx");
            var id = Guid.NewGuid();
            var timestamp = new DateTimeOffset(2026, 10, 9, 21, 30, 0, TimeSpan.FromHours(3));
            try
            {
                XlsxWrt.Write(
                    path,
                    "Products",
                    new[]
                    {
                        new XlsxWrt.Col { Header = "Product ID" },
                        new XlsxWrt.Col { Header = "Created at" }
                    },
                    new IReadOnlyList<object?>[]
                    {
                        new object?[] { id, timestamp }
                    },
                    rtl: true);

                using var archive = ZipFile.OpenRead(path);
                using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var worksheet = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var cells = worksheet.Root!.Element(ns + "sheetData")!
                    .Elements(ns + "row").Skip(1).Single().Elements(ns + "c").ToArray();

                Assert.All(cells, cell => Assert.Equal("inlineStr", (string?)cell.Attribute("t")));
                Assert.Contains(id.ToString(), cells[0].Value);
                Assert.Contains(timestamp.ToString(null, System.Globalization.CultureInfo.InvariantCulture), cells[1].Value);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void JournalEntry_ShouldAcceptBalancedLines()
        {
            var entry = new JournalEntry(new DateOnly(2026, 8, 21), "Opening balance");
            entry.AddLine(Guid.NewGuid(), debit: 1_000m, credit: 0m);
            entry.AddLine(Guid.NewGuid(), debit: 0m, credit: 1_000m);

            entry.EnsureBalanced();
        }

        [Fact]
        public void JournalEntry_ShouldRejectUnbalancedLines()
        {
            var entry = new JournalEntry(new DateOnly(2026, 8, 21), "Unbalanced entry");
            entry.AddLine(Guid.NewGuid(), debit: 1_000m, credit: 0m);
            entry.AddLine(Guid.NewGuid(), debit: 0m, credit: 900m);

            Assert.Throws<InvalidOperationException>(() => entry.EnsureBalanced());
        }

        [Fact]
        public void JournalLine_ShouldRejectDebitAndCreditOnTheSameLine()
        {
            Assert.Throws<ArgumentException>(() =>
                new JournalLine(Guid.NewGuid(), debit: 10m, credit: 10m));
        }

        [Fact]
        public void JournalEntry_ShouldSupportPostingAndReversalOnlyOnce()
        {
            var entry = new JournalEntry(new DateOnly(2026, 8, 21), "Reversible entry");
            entry.AddLine(Guid.NewGuid(), debit: 100m, credit: 0m);
            entry.AddLine(Guid.NewGuid(), debit: 0m, credit: 100m);

            entry.Post();
            Assert.Equal(JournalEntryStatus.Posted, entry.Status);

            entry.Reverse();
            Assert.Equal(JournalEntryStatus.Reversed, entry.Status);
            Assert.Throws<InvalidOperationException>(() => entry.Post());
        }

        [Fact]
        public void UserPreferences_ShouldNormalizeRegionalAndWindowSettings()
        {
            var preferences = new UsrPref
            {
                Lang = "fr-FR",
                Theme = "unknown",
                CurrencyCode = "sar-random",
                DateFormat = "invalid",
                DecimalDigits = 9,
                WinW = 100,
                WinH = 500
            };

            preferences.Normalize();

            Assert.Equal(LangDirSv.DefaultLanguageCode, preferences.Lang);
            Assert.Equal("Dark", preferences.Theme);
            Assert.Equal("SAR", preferences.CurrencyCode);
            Assert.Equal("dd/MM/yyyy", preferences.DateFormat);
            Assert.Equal(4, preferences.DecimalDigits);
            Assert.Equal(800, preferences.WinW);
            Assert.Equal(600, preferences.WinH);
        }

        [Fact]
        public async Task JournalRepository_ShouldPersistHeaderAndLineFieldsThroughReversal()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-journal-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();
            const string companyCode = "JRN-ROUNDTRIP";
            const string actor = "journal-roundtrip-test";

            try
            {
                ApplySqliteMigrations(databasePath);

                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    using var insertCompany = connection.CreateCommand();
                    insertCompany.CommandText = """
                        INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en)
                        VALUES ($id, $code, 'شركة اختبار', 'Test Company');
                        """;
                    insertCompany.Parameters.AddWithValue("$id", companyId.ToString("D"));
                    insertCompany.Parameters.AddWithValue("$code", companyCode);
                    await insertCompany.ExecuteNonQueryAsync();
                }

                var structure = new ShouTech.Infrastructure.AccountingStructureRepository("SQLite", connectionString);
                await structure.CreateFiscalYearAsync(companyId, 2026, actor);
                var debitAccount = await structure.CreateAccountAsync(companyId, "1101", "Cash", "ASSET");
                var creditAccount = await structure.CreateAccountAsync(companyId, "4101", "Sales", "REVENUE");

                var repository = new ShouTech.Infrastructure.JournalRepository("SQLite", connectionString);
                Assert.Equal(companyId, await repository.ResolveCompanyIdAsync(companyCode));
                Assert.Equal(debitAccount, await repository.ResolveAccountIdAsync(companyId, "1101"));
                Assert.Equal("Cash", await repository.ResolveAccountNameAsync(companyId, "1101"));
                var service = new JournalService(repository);

                var journalId = await service.CreateDraftAsync(
                    companyId,
                    new DateOnly(2026, 10, 16),
                    "Journal header round trip",
                    actor,
                    new[]
                    {
                        new JournalLineRequest(debitAccount, 12.5m, 0m, "Debit note", "CC-10"),
                        new JournalLineRequest(creditAccount, 0m, 12.5m, "Credit note", "CC-20")
                    },
                    referenceNumber: "REF-001",
                    currencyCode: "JOD",
                    exchangeRate: 0.7095m,
                    documentNumber: "JV-ROUNDTRIP-001");

                var loadedSnapshot = await service.FindByDocumentNumberAsync(companyId, "JV-ROUNDTRIP-001");
                Assert.NotNull(loadedSnapshot);
                Assert.Equal(journalId, loadedSnapshot!.Id);
                Assert.Equal(companyId, loadedSnapshot.CompanyId);
                Assert.Equal(new DateOnly(2026, 10, 16), loadedSnapshot.EntryDate);
                Assert.Equal("Journal header round trip", loadedSnapshot.Description);
                Assert.Equal("REF-001", loadedSnapshot.ReferenceNumber);
                Assert.Equal("JOD", loadedSnapshot.CurrencyCode);
                Assert.Equal(0.7095m, loadedSnapshot.ExchangeRate);
                Assert.Equal(JournalEntryStatus.Draft, loadedSnapshot.Status);
                Assert.Collection(loadedSnapshot.Lines,
                    line =>
                    {
                        Assert.Equal("1101", line.AccountCode);
                        Assert.Equal("Cash", line.AccountName);
                        Assert.Equal(12.5m, line.Debit);
                        Assert.Equal(0m, line.Credit);
                        Assert.Equal("CC-10", line.CostCenter);
                        Assert.Equal("Debit note", line.Note);
                    },
                    line =>
                    {
                        Assert.Equal("4101", line.AccountCode);
                        Assert.Equal("Sales", line.AccountName);
                        Assert.Equal(0m, line.Debit);
                        Assert.Equal(12.5m, line.Credit);
                        Assert.Equal("CC-20", line.CostCenter);
                        Assert.Equal("Credit note", line.Note);
                    });

                await Assert.ThrowsAsync<SqliteException>(async () =>
                    await service.CreateDraftAsync(
                        companyId,
                        new DateOnly(2026, 10, 16),
                        "Duplicate journal document number",
                        actor,
                        new[]
                        {
                            new JournalLineRequest(debitAccount, 1m, 0m),
                            new JournalLineRequest(creditAccount, 0m, 1m)
                        },
                        documentNumber: "JV-ROUNDTRIP-001"));

                await using (var verify = new SqliteConnection(connectionString))
                {
                    await verify.OpenAsync();
                    using var header = verify.CreateCommand();
                    header.CommandText = """
                        SELECT reference_number, currency_code, exchange_rate, document_number, status
                        FROM journal_entries WHERE journal_entry_id = $id;
                        """;
                    header.Parameters.AddWithValue("$id", journalId.ToString("D"));
                    using var reader = await header.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal("REF-001", reader.GetString(0));
                    Assert.Equal("JOD", reader.GetString(1));
                    Assert.Equal(0.7095m, Convert.ToDecimal(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Equal("JV-ROUNDTRIP-001", reader.GetString(3));
                    Assert.Equal((int)JournalEntryStatus.Draft, reader.GetInt32(4));
                    await reader.DisposeAsync();

                    using var lines = verify.CreateCommand();
                    lines.CommandText = """
                        SELECT cost_center, note, debit, credit
                        FROM journal_entry_lines WHERE journal_entry_id = $id ORDER BY line_number;
                        """;
                    lines.Parameters.AddWithValue("$id", journalId.ToString("D"));
                    using var lineReader = await lines.ExecuteReaderAsync();
                    Assert.True(await lineReader.ReadAsync());
                    Assert.Equal("CC-10", lineReader.GetString(0));
                    Assert.Equal("Debit note", lineReader.GetString(1));
                    Assert.Equal(12.5m, Convert.ToDecimal(lineReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Equal(0m, Convert.ToDecimal(lineReader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(await lineReader.ReadAsync());
                    Assert.Equal("CC-20", lineReader.GetString(0));
                    Assert.Equal("Credit note", lineReader.GetString(1));
                }

                await service.PostAsync(journalId, actor);
                var reversalId = await service.ReverseAsync(journalId, actor, "round-trip test reversal");
                var loadedAfterReversal = await service.FindByDocumentNumberAsync(companyId, "JV-ROUNDTRIP-001");
                Assert.NotNull(loadedAfterReversal);
                Assert.Equal(JournalEntryStatus.Reversed, loadedAfterReversal!.Status);
                Assert.Equal("REF-001", loadedAfterReversal.ReferenceNumber);
                Assert.Equal("JOD", loadedAfterReversal.CurrencyCode);
                Assert.Equal(0.7095m, loadedAfterReversal.ExchangeRate);
                Assert.Equal("CC-10", loadedAfterReversal.Lines[0].CostCenter);
                Assert.Equal("CC-20", loadedAfterReversal.Lines[1].CostCenter);

                await using (var verify = new SqliteConnection(connectionString))
                {
                    await verify.OpenAsync();
                    using var originalStatus = verify.CreateCommand();
                    originalStatus.CommandText = "SELECT status FROM journal_entries WHERE journal_entry_id=$id;";
                    originalStatus.Parameters.AddWithValue("$id", journalId.ToString("D"));
                    Assert.Equal((int)JournalEntryStatus.Reversed,
                        Convert.ToInt32(await originalStatus.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

                    using var reversal = verify.CreateCommand();
                    reversal.CommandText = """
                        SELECT reference_number, currency_code, exchange_rate, document_number, status
                        FROM journal_entries WHERE journal_entry_id=$id;
                        """;
                    reversal.Parameters.AddWithValue("$id", reversalId.ToString("D"));
                    using var reversalReader = await reversal.ExecuteReaderAsync();
                    Assert.True(await reversalReader.ReadAsync());
                    Assert.Equal("REF-001", reversalReader.GetString(0));
                    Assert.Equal("JOD", reversalReader.GetString(1));
                    Assert.Equal(0.7095m, Convert.ToDecimal(reversalReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.StartsWith("REV-", reversalReader.GetString(3));
                    Assert.Equal((int)JournalEntryStatus.Posted, reversalReader.GetInt32(4));
                    await reversalReader.DisposeAsync();

                    using var reversedLines = verify.CreateCommand();
                    reversedLines.CommandText = """
                        SELECT cost_center, debit, credit
                        FROM journal_entry_lines WHERE journal_entry_id=$id ORDER BY line_number;
                        """;
                    reversedLines.Parameters.AddWithValue("$id", reversalId.ToString("D"));
                    using var reversedLineReader = await reversedLines.ExecuteReaderAsync();
                    Assert.True(await reversedLineReader.ReadAsync());
                    Assert.Equal("CC-10", reversedLineReader.GetString(0));
                    Assert.Equal(0m, Convert.ToDecimal(reversedLineReader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Equal(12.5m, Convert.ToDecimal(reversedLineReader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
            }
        }

        [Fact]
        public async Task JournalService_ShouldPersistOnlyBalancedDrafts()
        {
            var repository = new InMemoryJournalRepository();
            var service = new JournalService(repository);
            var debitAccount = Guid.NewGuid();
            var creditAccount = Guid.NewGuid();

            var id = await service.CreateDraftAsync(
                Guid.NewGuid(),
                new DateOnly(2026, 8, 21),
                "Sale transaction",
                "user-001",
                new[]
                {
                    new JournalLineRequest(debitAccount, 250m, 0m, CostCenter: "CC-100"),
                    new JournalLineRequest(creditAccount, 0m, 250m, CostCenter: "CC-200")
                });

            Assert.Equal(id, repository.SavedEntry?.Id);
            Assert.Collection(repository.SavedEntry!.Lines,
                line => Assert.Equal("CC-100", line.CostCenter),
                line => Assert.Equal("CC-200", line.CostCenter));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDraftAsync(
                Guid.NewGuid(),
                new DateOnly(2026, 8, 21),
                "Invalid transaction",
                "user-001",
                new[] { new JournalLineRequest(debitAccount, 250m, 0m) }));
        }

        [Fact]
        public void MoveMaterialHistory_ShouldMergeReferencesAndBalancesWithoutChangingMovementDetails()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-merge-{Guid.NewGuid():N}.sqlite3");
            var settingsField = typeof(AppCfgSvc).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Static)!;
            var schemaReadyField = typeof(DocSchema).GetField("_validated", BindingFlags.NonPublic | BindingFlags.Static)!;
            var oldSettings = settingsField.GetValue(null);
            var oldSchemaReady = schemaReadyField.GetValue(null);
            var oldCompany = AppState.CurrentCompanyCode;
            const string company = "TEST-MERGE";
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
                AppState.CurrentCompanyCode = company;

                schemaReadyField.SetValue(null, false);
                ApplySqliteMigrations(databasePath);
                typeof(StkPost).Assembly
                    .GetType("ShouTech.App.Services.MatRepo")!
                    .GetMethod("EnsureSchema", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, null);
                DocSchema.Ensure();
                using (var connection = new SqliteConnection($"Data Source={databasePath}"))
                {
                    connection.Open();
                    Execute(connection, """
                        INSERT INTO materials (company_id, code, name_ar, name_en, default_unit)
                        VALUES
                          ('TEST-MERGE', 'SRC', 'Source name', 'Source name EN', 'kg'),
                          ('TEST-MERGE', 'DST', 'Target name', 'Target name EN', 'kg');
                        INSERT INTO sales_documents (company_id, doc_kind, doc_no, doc_date)
                        VALUES ('TEST-MERGE', 'SAL', 'S-1', '2024-01-02');
                        INSERT INTO purchase_documents (company_id, doc_kind, doc_no, doc_date)
                        VALUES ('TEST-MERGE', 'PUR', 'P-1', '2024-01-03');
                        INSERT INTO sales_document_lines
                          (doc_id, line_no, material_id, material_code, description, unit, qty,
                           unit_price, discount_pct, line_subtotal, line_total)
                        VALUES
                          (1, 1, 1, 'SRC', 'Source name', 'kg', 2, 10, 5, 19, 19),
                          (1, 2, 2, 'DST', 'Custom invoice description', 'kg', 1, 20, 0, 20, 20);
                        INSERT INTO purchase_document_lines
                          (doc_id, line_no, material_id, material_code, description, unit, qty,
                           unit_price, discount_pct, line_subtotal, line_total)
                        VALUES (1, 1, 1, 'SRC', 'Custom purchase description', 'kg', 3, 4, 0, 12, 12);
                        INSERT INTO material_stock_movements
                          (company_id, warehouse_id, material_id, movement_date, movement_type,
                           document_no, qty_in, qty_out, unit_cost, note)
                        VALUES
                          ('TEST-MERGE', 1, 1, '2024-01-02', 'SAL', 'S-1', 0, 2, 10, 'Keep movement note'),
                          ('OTHER-COMPANY', 1, 1, '2024-01-04', 'SAL', 'OTHER-1', 0, 1, 8, 'Other company');
                        INSERT INTO material_stock_balances
                          (company_id, warehouse_id, material_id, available_qty, reserved_qty,
                           average_cost, last_cost, is_deleted)
                        VALUES
                          ('TEST-MERGE', 1, 1, 4, 2, 3, 3, 0),
                          ('TEST-MERGE', 1, 2, 6, 1, 5, 5, 0);
                        """);
                }

                using (var connection = new SqliteConnection($"Data Source={databasePath}"))
                {
                    connection.Open();
                    Execute(connection, "UPDATE materials SET default_unit='box' WHERE material_id=2;");
                }
                var unitError = Assert.Throws<StkPost.MaterialHistoryMoveException>(
                    () => StkPost.MoveMaterialHistory(1, 2));
                Assert.Equal("INV-MRG-003", unitError.ErrorCode);
                using (var connection = new SqliteConnection($"Data Source={databasePath}"))
                {
                    connection.Open();
                    Assert.Equal(1L, ScalarLong(connection,
                        "SELECT COUNT(1) FROM sales_document_lines WHERE material_id=1;"));
                    Execute(connection, "UPDATE materials SET default_unit='kg' WHERE material_id=2;");
                }

                var result = StkPost.MoveMaterialHistory(1, 2);

                using var verify = new SqliteConnection($"Data Source={databasePath}");
                verify.Open();
                Assert.Equal(2, result.DocumentLines);
                Assert.Equal(1, result.Movements);
                Assert.Equal(2, ScalarLong(verify,
                    "SELECT COUNT(1) FROM sales_document_lines WHERE material_id=2 AND material_code='DST';"));
                Assert.Equal("Target name", ScalarText(verify,
                    "SELECT description FROM sales_document_lines WHERE line_no=1;"));
                Assert.Equal("Custom invoice description", ScalarText(verify,
                    "SELECT description FROM sales_document_lines WHERE line_no=2;"));
                Assert.Equal("Custom purchase description", ScalarText(verify,
                    "SELECT description FROM purchase_document_lines WHERE line_no=1;"));
                Assert.Equal(10m, ScalarDecimal(verify,
                    "SELECT unit_price FROM sales_document_lines WHERE line_no=1;"));
                Assert.Equal(5m, ScalarDecimal(verify,
                    "SELECT discount_pct FROM sales_document_lines WHERE line_no=1;"));
                Assert.Equal("2024-01-02", ScalarText(verify,
                    "SELECT movement_date FROM material_stock_movements WHERE material_id=2;"));
                Assert.Equal("Keep movement note", ScalarText(verify,
                    "SELECT note FROM material_stock_movements WHERE material_id=2;"));
                Assert.Equal(1L, ScalarLong(verify,
                    "SELECT COUNT(1) FROM material_stock_movements WHERE company_id='OTHER-COMPANY' AND material_id=1 AND note='Other company';"));
                Assert.Equal(10m, ScalarDecimal(verify,
                    "SELECT available_qty FROM material_stock_balances WHERE material_id=2 AND is_deleted=0;"));
                Assert.Equal(3m, ScalarDecimal(verify,
                    "SELECT reserved_qty FROM material_stock_balances WHERE material_id=2 AND is_deleted=0;"));
                Assert.Equal(4.2m, ScalarDecimal(verify,
                    "SELECT average_cost FROM material_stock_balances WHERE material_id=2 AND is_deleted=0;"));
                Assert.Equal(0L, ScalarLong(verify,
                    "SELECT COUNT(1) FROM material_stock_movements WHERE company_id='TEST-MERGE' AND material_id=1;"));
                Assert.Equal(0L, ScalarLong(verify,
                    "SELECT COUNT(1) FROM sales_document_lines WHERE material_id=1;"));
                Assert.Equal(0L, ScalarLong(verify,
                    "SELECT COUNT(1) FROM purchase_document_lines WHERE material_id=1;"));
                Assert.Equal(0L, ScalarLong(verify,
                    "SELECT COUNT(1) FROM material_stock_balances WHERE material_id=1 AND is_deleted=0;"));
                verify.Close();
            }
            finally
            {
                settingsField.SetValue(null, oldSettings);
                schemaReadyField.SetValue(null, oldSchemaReady);
                AppState.CurrentCompanyCode = oldCompany;
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath))
                    File.Delete(databasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var sidecar = databasePath + suffix;
                    if (File.Exists(sidecar))
                        File.Delete(sidecar);
                }
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
                $"Applying SQLite DBA migrations failed (exit {process.ExitCode}).\\n{output}\\n{errors}");
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ScalarText(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        private static decimal ScalarDecimal(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class InMemoryJournalRepository : IJournalRepository
        {
            public JournalEntry? SavedEntry { get; private set; }

            public Task<JournalEntrySnapshot?> FindByDocumentNumberAsync(Guid companyId, string documentNumber, CancellationToken cancellationToken = default) =>
                Task.FromResult<JournalEntrySnapshot?>(null);

            public Task SaveDraftAsync(Guid companyId, JournalEntry entry, string userId, CancellationToken cancellationToken = default)
            {
                SavedEntry = entry;
                return Task.CompletedTask;
            }

            public Task PostAsync(Guid journalEntryId, string userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<Guid> ReverseAsync(Guid journalEntryId, string userId, string? details = null, CancellationToken cancellationToken 	    = default) =>
    		   Task.FromResult(Guid.NewGuid());
        }
    }
}
