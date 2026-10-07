namespace LumaChart.Storage;

/// <summary>
/// Persists only a revocable server session token in platform secure storage.
/// Implementations must fail closed and must never fall back to plaintext.
/// </summary>
public interface ISessionTokenStore
{
    ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(string token, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(CancellationToken cancellationToken = default);
}
