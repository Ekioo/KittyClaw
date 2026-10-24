namespace KittyClaw.Web.Services;

/// <summary>Unsaved owner input kept by the ticket panel across responsive layout changes.</summary>
public sealed class TicketQuestionAnswerDraft
{
    public HashSet<string> SelectedOptions { get; } = [];
    public string FreeText { get; set; } = "";
}
