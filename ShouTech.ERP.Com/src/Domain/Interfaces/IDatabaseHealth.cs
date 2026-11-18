namespace ShouTech.Domain.Interfaces;

public sealed record DatabaseHealthResult(bool IsAvailable, string Provider, string Message, TimeSpan? Latency);

public interface IDatabaseHealth
{
    Task<DatabaseHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}
