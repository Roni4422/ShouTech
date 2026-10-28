// ========================================================================
// FILE: svc/ServiceHealth.cs
// PROJECT: SHOUTECH ERP V10 — Background Windows Service
// ========================================================================

using System.Text.Json;

namespace ShouTech.Svc;

public sealed class ServiceHealth
{
    private static readonly Action<ILogger, Exception?> HeartbeatWriteSkipped =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1, nameof(HeartbeatWriteSkipped)),
            "Heartbeat write skipped (best-effort).");

    private long _messagesSent;
    private long _messagesFailed;
    private long _syncBatches;
    private long _licenseViolations;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatUtc { get; private set; } = DateTimeOffset.UtcNow;

    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long MessagesFailed => Interlocked.Read(ref _messagesFailed);
    public long SyncBatches => Interlocked.Read(ref _syncBatches);
    public long LicenseViolations => Interlocked.Read(ref _licenseViolations);

    public void IncSent() => Interlocked.Increment(ref _messagesSent);
    public void IncFailed() => Interlocked.Increment(ref _messagesFailed);
    public void IncSyncBatch() => Interlocked.Increment(ref _syncBatches);
    public void IncLicenseViolation() => Interlocked.Increment(ref _licenseViolations);

    public void WriteHeartbeat(ILogger logger)
    {
        try
        {
            LastHeartbeatUtc = DateTimeOffset.UtcNow;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ShouTech", "svc");
            Directory.CreateDirectory(dir);

            var payload = JsonSerializer.Serialize(new
            {
                service = "ShouTech ERP Sync Service",
                version = "10.6.0",
                startedAtUtc = StartedAtUtc,
                heartbeatUtc = LastHeartbeatUtc,
                messagesSent = MessagesSent,
                messagesFailed = MessagesFailed,
                syncBatches = SyncBatches,
                licenseViolations = LicenseViolations
            });

            var path = Path.Combine(dir, "heartbeat.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            HeartbeatWriteSkipped(logger, ex);
        }
    }
}
