using LumaChart.Storage;

namespace LumaChart.Online;

/// <summary>
/// Keeps the in-memory bearer token aligned with platform secure storage. The
/// token becomes usable only after a successful secure write.
/// </summary>
public sealed class OnlineSessionTokenState(ISessionTokenStore store)
{
    private readonly ISessionTokenStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly SemaphoreSlim gate = new(1, 1);

    public string? CurrentToken { get; private set; }

    public async ValueTask<string?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CurrentToken = null;
            var token = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return null;
            CurrentToken = token;
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask StoreAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A session token cannot be empty.", nameof(token));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CurrentToken = null;
            try
            {
                await store.SaveAsync(token, cancellationToken).ConfigureAwait(false);
                CurrentToken = token;
            }
            catch (Exception saveException)
            {
                try
                {
                    await store.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception deleteException)
                {
                    throw new AggregateException(
                        "The session token could not be stored or removed securely.",
                        saveException,
                        deleteException);
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CurrentToken = null;
            await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Clears the token only while it is still the one a failed request used. A late
    /// 401 for an old token must not delete a newer login stored in the meantime.
    /// The comparison runs under the same gate as Store/Clear, so it is atomic.
    /// </summary>
    public async ValueTask<bool> ClearIfCurrentAsync(
        string expectedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(CurrentToken, expectedToken, StringComparison.Ordinal)) return false;
            CurrentToken = null;
            await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }
}
