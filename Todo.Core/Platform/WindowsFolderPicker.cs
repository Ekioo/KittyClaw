using System.Diagnostics;
using System.Runtime.Versioning;

namespace Todo.Core.Platform;

/// <summary>
/// Windows implementation via PowerShell + System.Windows.Forms.FolderBrowserDialog.
/// Works for the local-host and MAUI-on-Windows deployment modes. Does nothing (returns
/// null) on non-Windows platforms, so it's safe to register unconditionally.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderPicker : IFolderPicker
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task<string?> PickFolderAsync(string? initialPath, CancellationToken ct)
    {
        if (!IsAvailable) return null;

        var safe = (initialPath ?? string.Empty).Replace("'", "''");
        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms | Out-Null
            $d = New-Object System.Windows.Forms.FolderBrowserDialog
            $d.Description = 'Choisir le dossier du projet'
            $d.ShowNewFolderButton = $true
            if ('{{safe}}' -and (Test-Path '{{safe}}')) { $d.SelectedPath = '{{safe}}' }
            if ($d.ShowDialog() -eq 'OK') { Write-Output $d.SelectedPath }
            """;

        var psi = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-STA");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var path = output.Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
