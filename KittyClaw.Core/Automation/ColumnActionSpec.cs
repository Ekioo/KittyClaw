using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittyClaw.Core.Automation;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetLabelsActionSpec), "setLabels")]
[JsonDerivedType(typeof(AddCommentActionSpec), "addComment")]
[JsonDerivedType(typeof(ExecutePowerShellActionSpec), "executePowerShell")]
[JsonDerivedType(typeof(CreateTicketActionSpec), "createTicket")]
[JsonDerivedType(typeof(HttpRequestActionSpec), "httpRequest")]
public abstract class ActionSpec
{
    [JsonIgnore]
    public abstract string UiTypeKey { get; }

    /// <summary>Round-trips unknown action fields so a save never strips hand-added keys.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class SetLabelsActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "setLabels";
    /// <summary>Label names to add to the ticket.</summary>
    public List<string> Add { get; set; } = new();
    /// <summary>Label names to remove from the ticket.</summary>
    public List<string> Remove { get; set; } = new();
}

public sealed class AddCommentActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "addComment";
    /// <summary>Comment content. Supports placeholders: {ticketId}, {ticketTitle}, {assignee}.</summary>
    public string Content { get; set; } = "";
    /// <summary>Author of the comment (member slug).</summary>
    public string Author { get; set; } = "";
}

/// <summary>
/// Creates a ticket from a column action or a scheduled task.
/// Supports date/time placeholders in Title and Description: {now} (date + time), {date} (today), {time}, {monday} (Monday of current week), {firstOfMonth}.
/// When <see cref="SkipIfExists"/> is true (default), creation is skipped if an open ticket with the resolved title already exists.
/// </summary>
public sealed class CreateTicketActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "createTicket";
    /// <summary>Ticket title. Supports {now}, {date}, {time}, {monday}, {firstOfMonth}.</summary>
    public string Title { get; set; } = "";
    /// <summary>Ticket description (optional). Supports {now}, {date}, {time}, {monday}, {firstOfMonth}.</summary>
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Todo";
    public string? AssignedTo { get; set; }
    public string Priority { get; set; } = "NiceToHave";
    /// <summary>Label names to attach to the new ticket.</summary>
    public List<string> Labels { get; set; } = new();
    public int? ParentId { get; set; }
    public string CreatedBy { get; set; } = "automation";
    /// <summary>Skip creation if an open ticket with the same resolved title already exists.</summary>
    public bool SkipIfExists { get; set; } = true;
}

/// <summary>
/// Sends an outbound HTTP request — webhooks and integrations (notify Discord/Slack on a status
/// change, trigger a CI job, ping an external service). Loopback and link-local targets
/// (including cloud metadata endpoints) are refused unless <see cref="AllowLocalTargets"/> is
/// set, so an action edited by an agent cannot probe the host's internal network.
/// </summary>
public sealed class HttpRequestActionSpec : ActionSpec
{
    public override string UiTypeKey => "httpRequest";
    /// <summary>One of GET, POST, PUT, PATCH, DELETE.</summary>
    public string Method { get; set; } = "POST";
    /// <summary>Target URL, http/https only. Supports {ticketId}, {ticketTitle}, {ticketStatus}, {assignee}.</summary>
    public string Url { get; set; } = "";
    /// <summary>Extra request headers. Values support the same placeholders and are never logged.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>Request body (empty = none). Supports the same placeholders.</summary>
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "application/json";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Opt-in: allow loopback/link-local targets (blocked by default — SSRF guard).</summary>
    public bool AllowLocalTargets { get; set; }
}

/// <summary>Runs a PowerShell script or file with optional arguments and timeout.</summary>
public sealed class ExecutePowerShellActionSpec : ActionSpec
{
    [JsonIgnore]
    public override string UiTypeKey => "executePowerShell";
    public string Script { get; set; } = "";
    public string? ScriptFile { get; set; }
    public List<string> Arguments { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
    public Dictionary<string, string> Env { get; set; } = new();
}
