namespace KittyClaw.Core.Models;

/// <summary>Ordered pipeline-column metadata and its visible root-ticket count.</summary>
public sealed record BoardColumnSummary(
    int ColumnId, int PipelineId, string PipelineName, string Name, string Color, int TicketCount);
