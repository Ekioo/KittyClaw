namespace KittyClaw.Web.Services;

/// <summary>
/// Prevents an older asynchronous refresh from replacing the result of a newer request.
/// Loading is deliberately not serialized so a slow request cannot delay the current state.
/// </summary>
public sealed class LatestRefreshGuard
{
    private readonly object _sync = new();
    private long _requestedVersion;

    public async Task<bool> ApplyLatestAsync<T>(Func<Task<T>> load, Action<T> apply)
    {
        long version;
        lock (_sync)
            version = ++_requestedVersion;
        var result = await load();

        lock (_sync)
        {
            if (version != _requestedVersion)
                return false;

            apply(result);
            return true;
        }
    }
}
