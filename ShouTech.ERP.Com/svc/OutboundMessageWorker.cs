using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.Svc;

/// <summary>Provider-neutral outbox dispatcher. Webhooks are supported; email/SMS remain queued for configured connectors.</summary>
public sealed class OutboundMessageWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);
    private const int BatchSize = 50;
    private static readonly Action<ILogger, Exception?> OutboxIdle =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1, nameof(OutboxIdle)),
            "Outbox worker is idle: SHOUTECH_DB_CONNECTION is not configured.");
    private static readonly Action<ILogger, Exception?> QueueCycleFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(QueueCycleFailed)),
            "Outbound queue cycle failed.");
    private static readonly Action<ILogger, long, int, Exception?> MessageFailedPermanently =
        LoggerMessage.Define<long, int>(
            LogLevel.Warning,
            new EventId(3, nameof(MessageFailedPermanently)),
            "Outbound message {MessageId} failed permanently after {Attempts} attempts.");

    private readonly ILogger<OutboundMessageWorker> _logger;
    private readonly ServiceHealth _health;

    public OutboundMessageWorker(ILogger<OutboundMessageWorker> logger, ServiceHealth health)
    {
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connectionString = Environment.GetEnvironmentVariable("SHOUTECH_DB_CONNECTION")
                    ?? Environment.GetEnvironmentVariable("SHOUTECH_CONNECTION_STRING");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    OutboxIdle(_logger, null);
                }
                else
                {
                    await DrainQueueAsync(connectionString, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { QueueCycleFailed(_logger, ex); }

            _health.WriteHeartbeat(_logger);
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DrainQueueAsync(string connectionString, CancellationToken ct)
    {
        var provider = NormalizeProvider(Environment.GetEnvironmentVariable("SHOUTECH_DB_PROVIDER") ?? "SQLite");
        await using var db = CreateConnection(provider, connectionString);
        await db.OpenAsync(ct).ConfigureAwait(false);
        // Recover messages abandoned by a process crash. HTTP dispatch has a 15-second timeout,
        // so a 15-minute lease gives a generous margin without leaving items stuck indefinitely.
        await using (var recover = db.CreateCommand())
        {
            recover.CommandText = provider == "SQLite"
                ? "UPDATE outbound_messages SET status = 'QUEUED', next_attempt_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP WHERE status = 'SENDING' AND updated_at <= datetime('now', '-15 minutes')"
                : "UPDATE outbound_messages SET status = 'QUEUED', next_attempt_at = CURRENT_TIMESTAMP, updated_at = CURRENT_TIMESTAMP WHERE status = 'SENDING' AND updated_at <= CURRENT_TIMESTAMP - INTERVAL '15 minutes'";
            await recover.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var rows = new List<OutboundRow>();
        await using (var command = db.CreateCommand())
        {
            command.CommandText = $"SELECT outbound_message_id, channel, recipient_address, subject, body, body_format, attempt_count, max_attempts FROM outbound_messages WHERE status = 'QUEUED' AND next_attempt_at <= {CurrentTimestamp(provider)} ORDER BY next_attempt_at, outbound_message_id LIMIT @limit";
            Add(command, "@limit", BatchSize);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new OutboundRow(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture)));
            }
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await using (var claim = db.CreateCommand())
            {
                claim.CommandText = "UPDATE outbound_messages SET status = 'SENDING', updated_at = CURRENT_TIMESTAMP WHERE outbound_message_id = @id AND status = 'QUEUED'";
                Add(claim, "@id", row.Id);
                if (await claim.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) continue;
            }

            var (ok, error) = await DispatchAsync(row, ct).ConfigureAwait(false);
            var attempts = row.AttemptCount + 1;
            await using var update = db.CreateCommand();
            if (ok)
            {
                update.CommandText = "UPDATE outbound_messages SET status = 'SENT', sent_at = CURRENT_TIMESTAMP, attempt_count = @attempts, last_error = NULL, updated_at = CURRENT_TIMESTAMP WHERE outbound_message_id = @id";
                _health.IncSent();
            }
            else
            {
                var exhausted = attempts >= row.MaxAttempts;
                var delayMinutes = Math.Min(Math.Pow(2, attempts), 360);
                var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
                update.CommandText = "UPDATE outbound_messages SET status = @status, attempt_count = @attempts, next_attempt_at = @next, last_error = @error, updated_at = CURRENT_TIMESTAMP WHERE outbound_message_id = @id";
                Add(update, "@status", exhausted ? "FAILED" : "QUEUED");
                Add(update, "@next", provider == "PostgreSQL" ? nextAttempt : nextAttempt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                Add(update, "@error", error is { Length: > 950 } ? error[..950] : error ?? "Delivery failed.");
                _health.IncFailed();
                if (exhausted) MessageFailedPermanently(_logger, row.Id, attempts, null);
            }
            Add(update, "@attempts", attempts);
            Add(update, "@id", row.Id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<(bool Ok, string? Error)> DispatchAsync(OutboundRow row, CancellationToken ct)
    {
        if (!string.Equals(row.Channel, "WEBHOOK", StringComparison.OrdinalIgnoreCase))
            return (false, $"{row.Channel} connector is not configured.");
        if (!Uri.TryCreate(row.RecipientAddress, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (false, "Webhook destination must be an absolute HTTP or HTTPS URL.");
        try
        {
            using var client = new HttpClient { Timeout = HttpTimeout };
            using var content = new StringContent(row.Body, Encoding.UTF8,
                row.BodyFormat.Equals("JSON", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/plain");
            using var response = await client.PostAsync(uri, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (true, null) : (false, $"HTTP {(int)response.StatusCode} ({response.StatusCode})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, "Webhook request timed out."); }
        catch (HttpRequestException ex) { return (false, ex.Message); }
    }

    private static DbConnection CreateConnection(string provider, string cs) => provider == "SQLite"
        ? new SqliteConnection(cs) : new NpgsqlConnection(cs);
    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported provider '{provider}'. Use SQLite or PostgreSQL.")
    };
    private static string CurrentTimestamp(string provider) => provider == "SQLite" ? "CURRENT_TIMESTAMP" : "CURRENT_TIMESTAMP";
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
    private sealed record OutboundRow(long Id, string Channel, string RecipientAddress, string? Subject, string Body, string BodyFormat, int AttemptCount, int MaxAttempts);
}
