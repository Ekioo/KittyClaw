using System.Text.RegularExpressions;

namespace KittyClaw.Web.Services;

internal static class RemoteAccessName
{
    private static readonly HashSet<string> Reserved = new(
        "www remote-api api admin app mail smtp imap pop pop3 ftp sftp ssh vpn status docs blog support help cdn assets static auth login account accounts billing checkout ns ns1 ns2 autodiscover autoconfig mta-sts webmail dev staging test".Split(' '), StringComparer.Ordinal);

    internal static string? Normalize(string? value)
    {
        var name = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return null;
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$")
            || name.StartsWith("kc-", StringComparison.Ordinal) || name.StartsWith("xn--", StringComparison.Ordinal)
            || Reserved.Contains(name)) throw new RemoteAccessException("RemoteNameInvalid");
        return name;
    }

    internal static string Hostname(RemoteAccessIdentity identity) =>
        $"{identity.Subdomain ?? $"kc-{identity.InstanceId}"}.kittyclaw.dev";

    internal static bool IsHostname(string value)
    {
        const string suffix = ".kittyclaw.dev";
        if (!value.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var name = value[..^suffix.Length];
        if (name.StartsWith("kc-", StringComparison.Ordinal))
            return Regex.IsMatch(name, "^kc-[a-z0-9]{6,40}$");
        try { return Normalize(name) == name; }
        catch (RemoteAccessException) { return false; }
    }
}
