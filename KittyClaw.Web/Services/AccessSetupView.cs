using System.Net;
using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

/// <summary>Native access controls live only in the local advanced setup page.</summary>
public static class AccessSetupView
{
    public static string Options(InstanceAccessMode selected, RemoteAccessStore remote, LocalizationService l)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = $"<fieldset class='access-modes'><legend>{E(l["AccessModeChoice"])}</legend>";
        foreach (var mode in Enum.GetValues<InstanceAccessMode>())
        {
            html += $"<label class='access-mode'><input type='radio' name='mode' value='{mode.ToString().ToLowerInvariant()}' {(mode == selected ? "checked" : "")} required>" +
                $"<span><strong>{E(l["AccessMode" + mode])}</strong><small>{E(l["AccessHint" + mode])}</small></span></label>";
        }
        html += "</fieldset><div class='access-internet-options'>";
        html += $"<p>{E(l["AccessInternetConfirm"])}</p>";
        if (!remote.Snapshot.Registered)
        {
            var identity = remote.Identity;
            var readOnly = identity is not null && !identity.NameRetryAllowed ? " readonly" : "";
            html += $"<label for='access-subdomain'>{E(l["RemoteName"])}</label><div class='access-hostname'><input id='access-subdomain' name='subdomain' value='{E(identity?.Subdomain ?? "")}' autocomplete='off' autocapitalize='none' spellcheck='false' pattern='[A-Za-z0-9][A-Za-z0-9-]{{1,30}}[A-Za-z0-9]' minlength='3' maxlength='32'{readOnly}><span>.kittyclaw.dev</span></div><p>{E(l["AccessDnsHint"])}</p>";
        }
        else html += $"<p>{E(l["AccessPublicAddress"])} <a href='{E(remote.Snapshot.Url!)}' target='_blank' rel='noopener noreferrer'>{E(remote.Snapshot.Url!)}</a></p>";
        html += $"<label class='access-consent'><input type='checkbox' name='consent' value='yes' {(remote.Snapshot.Enabled ? "checked" : "")}><span>{E(l["RemoteConsent"])}</span></label></div>";
        return html;
    }
    public const string Script = """
        (() => {
            for (const form of document.querySelectorAll('form.access-settings-form')) {
                let pending = false;
                const selected = () => form.querySelector('input[name=mode]:checked')?.value;
                const consent = form.querySelector('input[name=consent]');
                const submit = () => {
                    if (pending || selected() === form.dataset.currentMode) return;
                    pending = true;
                    form.setAttribute('aria-busy', 'true');
                    form.inert = true;
                    HTMLFormElement.prototype.submit.call(form);
                };
                form.addEventListener('change', event => {
                    if (pending) return;
                    if (event.target.name === 'mode') {
                        if (selected() === 'internet') {
                            if (form.dataset.currentMode !== 'internet') consent.checked = false;
                        } else submit();
                    } else if (event.target === consent && consent.checked && selected() === 'internet') submit();
                });
                form.addEventListener('submit', event => {
                    event.preventDefault();
                    if (selected() === 'internet' && !consent.checked) { consent.focus(); return; }
                    submit();
                });
            }
        })();
        """;
    public const string Styles = """
        .access-modes{border:0;margin:20px 0;padding:0;min-width:0}.access-modes legend{font-weight:600;margin-bottom:12px}.access-mode{display:flex;align-items:flex-start;gap:14px;min-height:64px;padding:16px;margin:0 0 10px;border:1px solid var(--border,#2e2e48);border-radius:10px;cursor:pointer;background:var(--surface2,#232338)}.access-mode:has(input:checked){border-color:#7c5cff;background:rgba(124,92,255,.12)}.access-mode input[type=radio],.access-consent input[type=checkbox]{flex:0 0 20px;width:20px;height:20px;min-height:20px;margin:3px 0 0;padding:0;accent-color:#7c5cff}.access-mode span{min-width:0}.access-mode strong,.access-mode small{display:block}.access-mode small{margin-top:4px;line-height:1.5;opacity:.8}.access-internet-options{border:1px solid var(--border,#2e2e48);border-radius:10px;padding:20px;margin-bottom:20px}.access-settings-form:not(:has(input[value=internet]:checked)) .access-internet-options{display:none}.access-hostname{display:flex;align-items:center;flex-wrap:wrap;gap:8px}.access-hostname input{min-width:0;max-width:100%;box-sizing:border-box}.access-consent{display:flex;align-items:flex-start;gap:12px;min-height:44px}.access-status{padding:12px 16px;border:1px solid var(--border,#2e2e48);border-radius:10px;margin-bottom:20px}.access-settings-form button{min-height:44px}.access-mode:focus-within{outline:2px solid #4aa8ff;outline-offset:3px}
        """;
}
