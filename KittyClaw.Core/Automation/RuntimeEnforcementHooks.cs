using System.Text;
using System.Text.Json;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Writes the per-run Claude Code hook bundle (settings.json + fail-closed gate script) that routes
/// every PreToolUse event through the KittyClaw approvals gate endpoint before the tool effect runs.
/// The script denies on any error, missing environment, or unanswered request — never fail-open.
/// </summary>
internal static class RuntimeEnforcementHooks
{
    internal const string SettingsFileName = "settings.json";

    /// <summary>Creates a temp directory with the hook bundle and returns its path. The caller owns
    /// deletion (AgentRunner removes it with the invocation's other temporary artifacts).</summary>
    internal static string WriteClaudeHookBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kittyclaw-enforce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string command;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(directory, "boundary-gate.ps1");
            File.WriteAllText(script, PowerShellScript, Encoding.UTF8);
            command = $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"";
        }
        else
        {
            var script = Path.Combine(directory, "boundary-gate.sh");
            File.WriteAllText(script, ShellScript, new UTF8Encoding(false));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            command = $"/bin/sh \"{script}\"";
        }

        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new { matcher = "*", hooks = new[] { new { type = "command", command, timeout = 600 } } },
                },
                PostToolUse = new[]
                {
                    new { matcher = "*", hooks = new[] { new { type = "command", command, timeout = 60 } } },
                },
            },
        };
        var settingsPath = Path.Combine(directory, SettingsFileName);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return directory;
    }

    // The script forwards the raw hook payload to the gate endpoint; classification, decision
    // lookup, receipt persistence and allow-once consumption all happen server-side so the shell
    // layer stays a dumb, fail-closed pipe. It always exits 0 with an explicit JSON verdict —
    // Claude Code treats some non-zero exits as non-blocking, so an explicit deny is the only
    // reliable fail-closed signal.
    private const string PowerShellScript = """
        $ErrorActionPreference = 'Stop'
        function Write-Verdict([string]$Decision, [string]$Reason) {
          $verdict = @{
            hookSpecificOutput = @{
              hookEventName = 'PreToolUse'
              permissionDecision = $Decision
              permissionDecisionReason = $Reason
            }
          } | ConvertTo-Json -Compress -Depth 6
          [Console]::Out.WriteLine($verdict)
        }
        $payload = [Console]::In.ReadToEnd()
        $isPost = $payload -match '"hook_event_name"\s*:\s*"PostToolUse"'
        try {
          $api = $env:KITTYCLAW_API_URL
          $slug = $env:KITTYCLAW_PROJECT_SLUG
          $runId = $env:KITTYCLAW_RUN_ID
          if ([string]::IsNullOrWhiteSpace($api) -or [string]::IsNullOrWhiteSpace($slug) -or [string]::IsNullOrWhiteSpace($runId)) {
            if (-not $isPost) { Write-Verdict 'deny' 'Fail-closed: enforcement environment variables are missing.' }
            exit 0
          }
          $gateUrl = "$api/api/projects/$slug/approvals/gate?runId=$runId"
          if ($isPost) {
            try { Invoke-RestMethod -Method Post -Uri $gateUrl -ContentType 'application/json' -Body $payload -TimeoutSec 30 | Out-Null } catch { }
            exit 0
          }
          $pollSeconds = 2
          if ($env:KITTYCLAW_ENFORCEMENT_POLL_SECONDS) { $pollSeconds = [int]$env:KITTYCLAW_ENFORCEMENT_POLL_SECONDS }
          $deadlineSeconds = 570
          if ($env:KITTYCLAW_ENFORCEMENT_DEADLINE_SECONDS) { $deadlineSeconds = [int]$env:KITTYCLAW_ENFORCEMENT_DEADLINE_SECONDS }
          $deadline = (Get-Date).AddSeconds($deadlineSeconds)
          while ($true) {
            $finalize = (Get-Date) -ge $deadline
            $url = $gateUrl
            if ($finalize) { $url = "$gateUrl&finalize=true" }
            $verdict = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json' -Body $payload -TimeoutSec 30
            if ($verdict.decision -eq 'allow') { Write-Verdict 'allow' ([string]$verdict.reason); exit 0 }
            if ($verdict.decision -eq 'deny' -or $finalize) { Write-Verdict 'deny' ([string]$verdict.reason); exit 0 }
            Start-Sleep -Seconds $pollSeconds
          }
        }
        catch {
          if (-not $isPost) { Write-Verdict 'deny' ('Fail-closed: ' + $_.Exception.Message) }
          exit 0
        }
        """;

    private const string ShellScript = """
        #!/bin/sh
        # Fail-closed KittyClaw boundary gate for Claude Code PreToolUse/PostToolUse hooks.
        payload=$(cat)
        emit() {
          printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"%s","permissionDecisionReason":"%s"}}\n' "$1" "$2"
        }
        case "$payload" in *'"hook_event_name"'*'PostToolUse'*) is_post=1 ;; *) is_post=0 ;; esac
        if [ -z "$KITTYCLAW_API_URL" ] || [ -z "$KITTYCLAW_PROJECT_SLUG" ] || [ -z "$KITTYCLAW_RUN_ID" ]; then
          [ "$is_post" = "0" ] && emit deny "Fail-closed: enforcement environment variables are missing."
          exit 0
        fi
        gate_url="$KITTYCLAW_API_URL/api/projects/$KITTYCLAW_PROJECT_SLUG/approvals/gate?runId=$KITTYCLAW_RUN_ID"
        if [ "$is_post" = "1" ]; then
          printf '%s' "$payload" | curl -sf -X POST -H 'Content-Type: application/json' --data-binary @- "$gate_url" >/dev/null 2>&1
          exit 0
        fi
        poll="${KITTYCLAW_ENFORCEMENT_POLL_SECONDS:-2}"
        deadline="${KITTYCLAW_ENFORCEMENT_DEADLINE_SECONDS:-570}"
        elapsed=0
        while :; do
          url="$gate_url"
          finalize=0
          if [ "$elapsed" -ge "$deadline" ]; then finalize=1; url="$gate_url&finalize=true"; fi
          response=$(printf '%s' "$payload" | curl -sf -X POST -H 'Content-Type: application/json' --data-binary @- "$url") || {
            emit deny "Fail-closed: approvals gate is unreachable."
            exit 0
          }
          case "$response" in
            *'"decision":"allow"'*) emit allow "Approved by KittyClaw runtime gate."; exit 0 ;;
            *'"decision":"deny"'*) emit deny "Denied by KittyClaw runtime gate."; exit 0 ;;
          esac
          if [ "$finalize" = "1" ]; then
            emit deny "Fail-closed: no approval decision within the enforcement window."
            exit 0
          fi
          sleep "$poll"
          elapsed=$((elapsed + poll))
        done
        """;
}
