using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Ribbon completion F1 coverage: generic definition-set CRUD against a real
/// migrated SQLite database (two sets + uniqueness guard + global payment
/// method seeds), installment schedule math, installment collection posting
/// a party_ledger credit, and loyalty earn/redeem/insufficient-balance.
/// </summary>
public class DefinitionServiceTests
{
    private const string Company = "TESTCO";

    // ------------------------------------------------------------- registry

    [Fact]
    public void Registry_ContainsAllF1Sets()
    {
        Assert.Equal(21, DefSets.All.Count);
        Assert.Equal("payment_methods", DefSets.Get(DefSets.PaymentMethods).Table);
        Assert.Equal("units_of_measure", DefSets.Get(DefSets.UnitsOfMeasure).Table);
        Assert.Equal("material_attributes", DefSets.Get(DefSets.MaterialSpecs).Table);
        Assert.Equal("VOUCHER", DefSets.Get(DefSets.VoucherTypes).FixedValues["set_kind"]);
        Assert.Equal("REP", DefSets.Get(DefSets.RepTargets).FixedValues["owner_kind"]);
        Assert.Equal("POS", DefSets.Get(DefSets.PosDiscounts).FixedValues["scope"]);
        Assert.Throws<ArgumentException>(() => DefSets.Get("NoSuchSet"));
    }

    // ------------------------------------------------------- schedule math

    [Fact]
    public void Schedule_EvenSplit_SumsToFinancedAmount()
    {
        var rows = DefinitionService.BuildSchedule(1000m, 100m, 3, new DateOnly(2026, 1, 15));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(300m, r.Amount));
        Assert.Equal(900m, rows.Sum(r => r.Amount));
        Assert.Equal(new DateOnly(2026, 1, 15), rows[0].DueDate);
        Assert.Equal(new DateOnly(2026, 3, 15), rows[2].DueDate);
    }

    [Fact]
    public void Schedule_RemainderGoesToLastRow_SumsExactly()
    {
        var rows = DefinitionService.BuildSchedule(100m, 0m, 3, new DateOnly(2026, 1, 1));
        Assert.Equal(33.33m, rows[0].Amount);
        Assert.Equal(33.33m, rows[1].Amount);
        Assert.Equal(33.34m, rows[2].Amount);
        Assert.Equal(100m, rows.Sum(r => r.Amount));
    }

    [Fact]
    public void Schedule_RejectsInvalidInputs()
    {
        Assert.Throws<InvalidOperationException>(() => DefinitionService.BuildSchedule(0m, 0m, 3, DateOnly.MinValue));
        Assert.Throws<InvalidOperationException>(() => DefinitionService.BuildSchedule(100m, 100m, 3, DateOnly.MinValue));
        Assert.Throws<InvalidOperationException>(() => DefinitionService.BuildSchedule(100m, 0m, 0, DateOnly.MinValue));
        Assert.Throws<InvalidOperationException>(() => DefinitionService.BuildSchedule(100m, -1m, 3, DateOnly.MinValue));
    }

    // ------------------------------------------------------------ generic CRUD

    [Fact]
    public async Task GenericCrud_TwoSets_UniquenessGuard_AndSoftDelete()
    {
        var (service, _, cleanup) = await CreateAsync();
        try
        {
            // Set 1: drivers
            var driverId = await service.SaveAsync(DefSets.Drivers, Company, null,
                Values(("code", "DRV-1"), ("name_en", "First Driver"), ("phone", "555")), "tester");
            Assert.False(string.IsNullOrWhiteSpace(driverId));
            var drivers = await service.ListAsync(DefSets.Drivers, Company);
            var driver = Assert.Single(drivers);
            Assert.Equal("DRV-1", driver.Values["code"]);
            Assert.True(driver.IsActive);

            // Update in place
            await service.SaveAsync(DefSets.Drivers, Company, driverId,
                Values(("code", "DRV-1"), ("name_en", "First Driver Updated"), ("vehicle_no", "ABC-123")), "tester");
            drivers = await service.ListAsync(DefSets.Drivers, Company);
            Assert.Equal("First Driver Updated", Assert.Single(drivers).Values["name_en"]);

            // Uniqueness guard: same code, different row
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.Drivers, Company, null, Values(("code", "DRV-1"), ("name_en", "Duplicate")), "tester"));

            // Activate/deactivate round trip
            await service.SetActiveAsync(DefSets.Drivers, Company, driverId, false, "tester");
            Assert.False((await service.ListAsync(DefSets.Drivers, Company)).Single().IsActive);
            await service.SetActiveAsync(DefSets.Drivers, Company, driverId, true, "tester");

            // Set 2: customer prices (composite unique key)
            var priceId = await service.SaveAsync(DefSets.CustomerPrices, Company, null,
                Values(("party_code", "501"), ("material_code", "MAT-1"), ("price", 42.5m)), "tester");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.CustomerPrices, Company, null,
                Values(("party_code", "501"), ("material_code", "MAT-1"), ("price", 1m)), "tester"));
            // Same material for another party is fine.
            await service.SaveAsync(DefSets.CustomerPrices, Company, null,
                Values(("party_code", "502"), ("material_code", "MAT-1"), ("price", 43m)), "tester");
            Assert.Equal(2, (await service.ListAsync(DefSets.CustomerPrices, Company)).Count);

            // Soft delete removes the row from the list; the other set is untouched.
            await service.SoftDeleteAsync(DefSets.CustomerPrices, Company, priceId, "tester");
            var prices = await service.ListAsync(DefSets.CustomerPrices, Company);
            Assert.Single(prices);
            Assert.Equal("502", prices[0].Values["party_code"]);
            Assert.Single(await service.ListAsync(DefSets.Drivers, Company));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task GenericCrud_Validation_RejectsBadValuesBeforePersistence()
    {
        var (service, _, cleanup) = await CreateAsync();
        try
        {
            // Missing required field
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.Drivers, Company, null, Values(("name_en", "No Code")), "tester"));
            // Discount percentage out of range
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.PosDiscounts, Company, null, Values(("target_code", "X"), ("discount_pct", 150m)), "tester"));
            // Kind outside the allowed list
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.PaymentMethods, Company, null,
                Values(("code", "BTC"), ("name_en", "Bitcoin"), ("kind", "Crypto")), "tester"));
            // Unknown column
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.Drivers, Company, null, Values(("code", "D9"), ("name_en", "X"), ("evil_column", "1")), "tester"));
            Assert.Empty(await service.ListAsync(DefSets.Drivers, Company));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task PaymentMethods_GlobalSeedsVisible_AndCompanyOverrideDoesNotMutateSeed()
    {
        var (service, _, cleanup) = await CreateAsync();
        try
        {
            var methods = await service.ListAsync(DefSets.PaymentMethods, Company);
            var codes = methods.Select(m => Convert.ToString(m.Values["code"])).ToList();
            Assert.Contains("CASH", codes);
            Assert.Contains("VISA", codes);
            Assert.Contains("MADA", codes);
            Assert.Contains("AMEX", codes);
            var visa = methods.Single(m => Convert.ToString(m.Values["code"]) == "VISA");
            Assert.Equal(string.Empty, visa.CompanyId); // shared global seed row
            Assert.Equal(true, visa.Values["is_pos_enabled"]);

            // Saving the seed creates a company override; the seed stays global.
            var overrideId = await service.SaveAsync(DefSets.PaymentMethods, Company, visa.Id,
                Values(("code", "VISA"), ("name_en", "Visa (company)"), ("kind", "Card"), ("commission_pct", 1.75m), ("is_pos_enabled", true)), "tester");
            Assert.NotEqual(visa.Id, overrideId);
            var after = await service.ListAsync(DefSets.PaymentMethods, Company);
            Assert.Equal(2, after.Count(m => Convert.ToString(m.Values["code"]) == "VISA"));
            Assert.Contains(after, m => m.CompanyId == Company && Convert.ToDecimal(m.Values["commission_pct"]) == 1.75m);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public async Task UnitsOfMeasure_LegacyTable_RoundTrips()
    {
        var (service, connectionString, cleanup) = await CreateAsync();
        try
        {
            // The public API receives the company code; the repository maps
            // the legacy UOM foreign key to the canonical company_id.
            var id = await service.SaveAsync(DefSets.UnitsOfMeasure, Company, null,
                Values(("uom_code", "PCS")), "tester");
            Assert.False(string.IsNullOrWhiteSpace(id));
            var rows = await service.ListAsync(DefSets.UnitsOfMeasure, Company);
            Assert.Contains(rows, r => Convert.ToString(r.Values["uom_code"]) == "PCS");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
                DefSets.UnitsOfMeasure, Company, null, Values(("uom_code", "PCS")), "tester"));
        }
        finally
        {
            cleanup();
        }
    }

    // ------------------------------------------------------------ installments

    [Fact]
    public async Task Installment_CreateAndCollect_WritesLedgerCredit_AndCompletesPlan()
    {
        var (service, connectionString, cleanup) = await CreateAsync();
        try
        {
            var plan = new InstallmentPlan
            {
                PartyCode = "501",
                DocRef = "INV-900",
                TotalAmount = 1000m,
                DownPayment = 100m,
                InstallmentCount = 3,
                StartDate = new DateOnly(2026, 2, 1)
            };
            var planId = await service.CreateInstallmentAsync(Company, plan, "tester");

            var schedule = await service.ListScheduleAsync(Company, planId);
            Assert.Equal(3, schedule.Count);
            Assert.Equal(900m, schedule.Sum(r => r.Amount));

            // Collect the first row in full.
            var collected = await service.CollectInstallmentAsync(
                Company, schedule[0].Id, 300m, new DateOnly(2026, 2, 1), "tester");
            Assert.Equal(ScheduleStatuses.Paid, collected.Status);
            Assert.Equal(300m, collected.PaidAmount);

            // The party_ledger credit exists (repository query via raw SQL).
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*), COALESCE(SUM(credit),0), COALESCE(SUM(debit),0) FROM party_ledger WHERE company_id = $company AND party_kind = 'C' AND party_id = 501 AND doc_kind = 'INS'";
                command.Parameters.AddWithValue("$company", Company);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(300m, reader.GetDecimal(1));
                Assert.Equal(0m, reader.GetDecimal(2));
            }

            // Overpayment and double-collection are rejected.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CollectInstallmentAsync(Company, schedule[1].Id, 301m, new DateOnly(2026, 3, 1), "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CollectInstallmentAsync(Company, schedule[0].Id, 1m, new DateOnly(2026, 3, 1), "tester"));

            // Partial then completing collections close the plan.
            var partial = await service.CollectInstallmentAsync(
                Company, schedule[1].Id, 100m, new DateOnly(2026, 3, 1), "tester");
            Assert.Equal(ScheduleStatuses.Partial, partial.Status);
            await service.CollectInstallmentAsync(Company, schedule[1].Id, 200m, new DateOnly(2026, 3, 5), "tester");
            await service.CollectInstallmentAsync(Company, schedule[2].Id, 300m, new DateOnly(2026, 4, 1), "tester");

            var plans = await service.ListInstallmentsAsync(Company, "501", null);
            var stored = Assert.Single(plans);
            Assert.Equal(InstallmentStatuses.Completed, stored.Status);
            Assert.Equal(900m, stored.PaidTotal);
            Assert.Equal(0m, stored.Remaining);

            // Search mode: filter by status / party.
            Assert.Single(await service.ListInstallmentsAsync(Company, null, InstallmentStatuses.Completed));
            Assert.Empty(await service.ListInstallmentsAsync(Company, "999", null));
        }
        finally
        {
            cleanup();
        }
    }

    // ----------------------------------------------------------------- loyalty

    [Fact]
    public async Task Loyalty_EarnRedeem_AndInsufficientBalanceBlocked()
    {
        var (service, _, cleanup) = await CreateAsync();
        try
        {
            Assert.Equal(100m, await service.EarnPointsAsync(Company, "CUST-9", 100m, "POS-1", null, "tester"));
            Assert.Equal(70m, await service.RedeemPointsAsync(Company, "CUST-9", 30m, "POS-2", null, "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RedeemPointsAsync(Company, "CUST-9", 71m, "POS-3", null, "tester"));
            // Redeeming for a party with no account is blocked too.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RedeemPointsAsync(Company, "GHOST", 1m, null, null, "tester"));

            var accounts = await service.ListLoyaltyAccountsAsync(Company);
            var account = Assert.Single(accounts);
            Assert.Equal(70m, account.PointsBalance);
            var entries = await service.ListLoyaltyEntriesAsync(Company, account.Id);
            Assert.Equal(2, entries.Count);
            Assert.Equal(70m, entries.Sum(e => e.Points));
        }
        finally
        {
            cleanup();
        }
    }

    // ---------------------------------------------------------------- delivery

    [Fact]
    public async Task Delivery_RequestFlow_AndDriverCashClose()
    {
        var (service, _, cleanup) = await CreateAsync();
        try
        {
            var id = await service.SaveDeliveryRequestAsync(Company, new DeliveryRequest
            {
                RequestDate = new DateOnly(2026, 2, 1),
                PartyCode = "501",
                Address = "Main Street 1",
                AreaCode = "NORTH",
                Amount = 250m
            }, "tester");
            var requests = await service.ListDeliveryRequestsAsync(Company);
            var request = Assert.Single(requests);
            Assert.Equal(1, request.DocNo); // auto-numbered
            Assert.Equal(DeliveryStatuses.Pending, request.Status);

            // Cannot deliver before assignment; assignment requires a driver.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetDeliveryStatusAsync(Company, id, DeliveryStatuses.Delivered, null, "tester"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetDeliveryStatusAsync(Company, id, DeliveryStatuses.Assigned, null, "tester"));
            await service.SetDeliveryStatusAsync(Company, id, DeliveryStatuses.Assigned, "DRV-1", "tester");
            await service.SetDeliveryStatusAsync(Company, id, DeliveryStatuses.Delivered, null, "tester");
            Assert.Equal(DeliveryStatuses.Delivered,
                (await service.ListDeliveryRequestsAsync(Company)).Single().Status);

            var settlementId = await service.CloseDriverCashAsync(
                Company, "DRV-1", new DateOnly(2026, 2, 1), 1000m, 25m, "tester");
            Assert.False(string.IsNullOrWhiteSpace(settlementId));
            var settlement = Assert.Single(await service.ListDriverSettlementsAsync(Company));
            Assert.Equal(975m, settlement.NetAmount);
            Assert.Equal("Closed", settlement.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CloseDriverCashAsync(Company, "DRV-1", new DateOnly(2026, 2, 1), 100m, 101m, "tester"));
        }
        finally
        {
            cleanup();
        }
    }

    // ----------------------------------------------------------------- helpers

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    private static async Task<(DefinitionService Service, string ConnectionString, Action Cleanup)> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"shoutech-definitions-{Guid.NewGuid():N}.sqlite3");
        var connectionString = $"Data Source={databasePath};";
        ApplySqliteMigrations(databasePath);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en)
                VALUES ($companyId, $companyCode, 'Definition test company', 'Definition test company');

                INSERT INTO parties (id, company_id, kind, code, name_en, ledger_id)
                VALUES ($partyId, $companyCode, 'C', '501', 'Installment customer', 501);
                """;
            seed.Parameters.AddWithValue("$companyId", Guid.NewGuid().ToString("D"));
            seed.Parameters.AddWithValue("$companyCode", Company);
            seed.Parameters.AddWithValue("$partyId", Guid.NewGuid().ToString("D"));
            await seed.ExecuteNonQueryAsync();
        }
        var repository = new DefinitionRepository("SQLite", connectionString);
        var service = new DefinitionService(repository);
        Action cleanup = () =>
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        };
        return (service, connectionString, cleanup);
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
