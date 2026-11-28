using System.Diagnostics;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

public sealed class TicketAttachmentLocationService(TicketAttachmentService attachments)
{
    internal static ProcessStartInfo BuildStartInfo(string path)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows()) { info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); }
        else if (OperatingSystem.IsMacOS()) { info.ArgumentList.Add("-R"); info.ArgumentList.Add(path); }
        else info.ArgumentList.Add(Path.GetDirectoryName(path)!);
        return info;
    }

    public async Task<bool> RevealAsync(string slug, int ticketId, string id, CancellationToken ct)
    {
        var attachment = await attachments.FindAsync(slug, ticketId, id, ct);
        if (attachment is null || !File.Exists(attachments.BlobPath(id))) return false;
        using var process = Process.Start(BuildStartInfo(attachments.BlobPath(id)));
        return true;
    }
}
