namespace Todo.Web.Services;

public sealed class BoardUpdateNotifier
{
    public event Action<string>? OnProjectUpdated;

    public void NotifyProjectUpdated(string slug) =>
        OnProjectUpdated?.Invoke(slug);
}
