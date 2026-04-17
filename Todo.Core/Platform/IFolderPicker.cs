namespace Todo.Core.Platform;

/// <summary>
/// Lets the server expose a native folder picker when the deployment mode supports it.
/// Registered by the host:
/// - Local / MAUI on Windows → WindowsFolderPicker (PowerShell FolderBrowserDialog).
/// - MAUI on other platforms → a MAUI-native picker wrapper (future).
/// - Cloud host → not registered (the UI falls back to a text input only).
/// </summary>
public interface IFolderPicker
{
    bool IsAvailable { get; }

    /// <summary>
    /// Show the picker. Returns the absolute path, or null if cancelled / unavailable.
    /// </summary>
    Task<string?> PickFolderAsync(string? initialPath, CancellationToken ct);
}
