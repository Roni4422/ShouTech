using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using Xunit;

namespace Kern.Tests
{
    /// <summary>
    /// Guard tests for journal reversal (issue #9):
    /// - drafts cannot be reversed,
    /// - an entry cannot be reversed twice,
    /// - reversal writes audit events for the original and compensating entries.
    /// </summary>
    public class JournalReversalGuardTests
    {
        [Fact]
        public async Task Reverse_ShouldRejectDraftEntries()
        {
            var context = await CreatePostedCandidateAsync(post: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.ReverseAsync(context.JournalId, "guard-test", "attempt to reverse a draft"));

            var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, GuardDocumentNumber);
            Assert.NotNull(loaded);
            Assert.Equal(JournalEntryStatus.Draft, loaded!.Status);
        }

        [Fact]
        public async Task Reverse_ShouldRejectAlreadyReversedEntries()
        {
            var context = await CreatePostedCandidateAsync(post: true);

            var firstReversal = await context.Service.ReverseAsync(context.JournalId, "guard-test", "first reversal");
            Assert.NotEqual(Guid.Empty, firstReversal);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Service.ReverseAsync(context.JournalId, "guard-test", "second reversal must be rejected"));

            var loaded = await context.Service.FindByDocumentNumberAsync(context.CompanyId, GuardDocumentNumber);
            Assert.NotNull(loaded);
            Assert.Equal(JournalEntryStatus.Reversed, loaded!.Status);
        }

        [Fact]
        public async Task Reverse_ShouldWriteAuditEventsForOriginalAndCompensatingEntries()
        {
            var context = await CreatePostedCandidateAsync(post: true);
            var reversalId = await context.Service.ReverseAsync(context.JournalId, "guard-test", "audit event verification");

            await using var connection = new SqliteConnection(context.ConnectionString);
            await connection.OpenAsync();

            Assert.Equal(2L, CountEvents(connection, context.JournalId));
            Assert.Equal(1L, CountEvents(connection, reversalId));

            long CountEvents(SqliteConnection c, Guid id)
            {
                using var command = c.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM journal_audit_events WHERE journal_entry_id = $id;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                return (long)command.ExecuteScalar()!;
            }
        }

        private const string GuardDocumentNumber = "JV-GUARD-001";
        private const string GuardCompanyCode = "JRN-GUARD";

        private sealed record GuardContext(
            JournalService Service,
            Guid CompanyId,
            Guid JournalId,
            string ConnectionString);

        private static async Task<GuardContext> CreatePostedCandidateAsync(bool post)
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-journal-guard-{Guid.NewGuid():N}.sqlite3");
            var connectionString = $"Data Source={databasePath};";
            var companyId = Guid.NewGuid();
            const string actor = "journal-guard-test";

            try
            {
                ApplySqliteMigrations(databasePath);

                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    using var insertCompany = connection.CreateCommand();
                    insertCompany.CommandText = """
                        INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en)
                        VALUES ($id, $code, 'شركة اختبار الحماية', 'Guard Test Company');
                        """;
                    insertCompany.Parameters.AddWithValue("$id", companyId.ToString("D"));
                    insertCompany.Parameters.AddWithValue("$code", GuardCompanyCode);
                    await insertCompany.ExecuteNonQueryAsync();
                }

                var structure = new ShouTech.Infrastructure.AccountingStructureRepository("SQLite", connectionString);
                await structure.CreateFiscalYearAsync(companyId, 2026, actor);
                var debitAccount = await structure.CreateAccountAsync(companyId, "1101", "Cash", "ASSET");
                var creditAccount = await structure.CreateAccountAsync(companyId, "4101", "Sales", "REVENUE");

                var repository = new ShouTech.Infrastructure.JournalRepository("SQLite", connectionString);
                var service = new JournalService(repository);

                var journalId = await service.CreateDraftAsync(
                    companyId,
                    new DateOnly(2026, 10, 16),
                    "Reversal guard candidate",
                    actor,
                    new[]
                    {
                        new JournalLineRequest(debitAccount, 25m, 0m),
                        new JournalLineRequest(creditAccount, 0m, 25m)
                    },
                    documentNumber: GuardDocumentNumber);

                if (post)
                    await service.PostAsync(journalId, actor);

                return new GuardContext(service, companyId, journalId, connectionString);
            }
            catch
            {
                Cleanup(databasePath);
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

        private static void Cleanup(string databasePath)
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
}
