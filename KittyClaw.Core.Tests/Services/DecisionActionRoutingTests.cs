using KittyClaw.Core.Models;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class DecisionActionRoutingTests
{
    [Fact]
    public void Resolve_UsesSourceProcessorRoutes_RegardlessOfColumnNamesOrOrder()
    {
        var correction = Column(3, "À corriger", 3);
        var source = Column(5, "Validation et intégration", 4);
        var approved = Column(6, "Livré", 5, ColumnRole.Success);
        var stopped = Column(7, "Abandonné", 6, ColumnRole.Failure);
        var ownerAction = Column(17, "Informations du propriétaire", 7, ColumnRole.OwnerAction);
        var processor = new ColumnProcessor
        {
            Id = 5,
            ColumnId = 5,
            Name = "Validation et intégration",
            DefaultTargetColumnId = approved.Id,
            Routes =
            [
                new(DecisionActionRouting.ApprovedOutcome, approved.Id),
                new(DecisionActionRouting.ChangesRequestedOutcome, correction.Id),
                new("needs_input", ownerAction.Id),
                new(DecisionActionRouting.AbandonedOutcome, stopped.Id),
            ]
        };
        var execution = new ColumnExecution
        {
            Id = "run-1",
            ProcessorId = processor.Id,
            TicketId = 181,
            Status = ColumnExecutionStatus.Completed,
            TargetColumnId = ownerAction.Id,
            Outcome = "needs_input",
        };

        var targets = DecisionActionRouting.Resolve(
            [correction, source, approved, stopped, ownerAction], [processor], [execution], ownerAction);

        Assert.Equal(approved.Id, targets.Accepted?.Id);
        Assert.Equal(correction.Id, targets.CorrectionRequested?.Id);
        Assert.Equal(stopped.Id, targets.Stopped?.Id);
    }

    [Fact]
    public void Resolve_ResumesProcessorThatReturnedToOwner_InsteadOfSkippingToItsSuccessTarget()
    {
        var publication = Column(12, "Web publication", 5);
        var ownerReview = Column(20, "Owner web review", 4, ColumnRole.OwnerAction);
        var linkedInDraft = Column(13, "LinkedIn draft", 6);
        var processor = new ColumnProcessor
        {
            Id = 4,
            ColumnId = publication.Id,
            Name = "Web publisher",
            DefaultTargetColumnId = linkedInDraft.Id,
            Routes =
            [
                new("published", linkedInDraft.Id),
                new("needs_owner", ownerReview.Id),
            ]
        };
        var execution = new ColumnExecution
        {
            Id = "publication-needs-owner",
            ProcessorId = processor.Id,
            TicketId = 254,
            Status = ColumnExecutionStatus.Completed,
            TargetColumnId = ownerReview.Id,
            Outcome = "needs_owner",
        };

        var targets = DecisionActionRouting.Resolve(
            [publication, ownerReview, linkedInDraft], [processor], [execution], ownerReview);

        Assert.Equal(publication.Id, targets.Accepted?.Id);
        Assert.NotEqual(linkedInDraft.Id, targets.Accepted?.Id);
    }

    [Fact]
    public void Resolve_DoesNotInventCorrectionDestination_WhenRouteIsMissing()
    {
        var resumed = Column(2, "Ready", 1);
        var ownerAction = Column(17, "Owner input", 7, ColumnRole.OwnerAction);
        var processor = new ColumnProcessor
        {
            Id = 1,
            ColumnId = 1,
            Name = "Qualification",
            DefaultTargetColumnId = resumed.Id,
            Routes = [new("qualified", resumed.Id), new("needs_input", ownerAction.Id)]
        };
        var execution = new ColumnExecution
        {
            Id = "run-2",
            ProcessorId = processor.Id,
            TicketId = 42,
            Status = ColumnExecutionStatus.Completed,
            TargetColumnId = ownerAction.Id,
        };

        var targets = DecisionActionRouting.Resolve(
            [resumed, ownerAction], [processor], [execution], ownerAction);

        Assert.Equal(resumed.Id, targets.Accepted?.Id);
        Assert.Null(targets.CorrectionRequested);
        Assert.Null(targets.Stopped);
    }

    [Fact]
    public void Resolve_IgnoresOlderExecutionFromAnotherProcessor()
    {
        var current = Column(17, "Owner input", 7, ColumnRole.OwnerAction);
        var oldTarget = Column(30, "Old correction", 2);
        var latestTarget = Column(31, "Current correction", 3);
        var oldProcessor = Processor(10, oldTarget.Id);
        var latestProcessor = Processor(11, latestTarget.Id);
        var oldExecution = Execution("old", oldProcessor.Id, current.Id, DateTime.UtcNow.AddHours(-1));
        var latestExecution = Execution("latest", latestProcessor.Id, current.Id, DateTime.UtcNow);

        var targets = DecisionActionRouting.Resolve(
            [current, oldTarget, latestTarget], [oldProcessor, latestProcessor],
            [oldExecution, latestExecution], current);

        Assert.Equal(latestTarget.Id, targets.CorrectionRequested?.Id);
    }

    [Fact]
    public void Resolve_UsesCurrentColumnRouting_WhenNoExecutionHistoryExists()
    {
        var current = Column(17, "Owner input", 7, ColumnRole.OwnerAction);
        var correction = Column(3, "Fix", 3);
        var processor = Processor(20, correction.Id);
        processor.ColumnId = current.Id;

        var targets = DecisionActionRouting.Resolve([current, correction], [processor], [], current);

        Assert.Equal(correction.Id, targets.CorrectionRequested?.Id);
    }

    private static BoardColumn Column(int id, string name, int order, ColumnRole role = ColumnRole.Normal) =>
        new() { Id = id, PipelineId = 1, Name = name, SortOrder = order, Role = role };

    private static ColumnProcessor Processor(int id, int correctionTarget) => new()
    {
        Id = id,
        ColumnId = id,
        Name = $"processor-{id}",
        Routes = [new(DecisionActionRouting.ChangesRequestedOutcome, correctionTarget)]
    };

    private static ColumnExecution Execution(string id, int processorId, int targetColumnId, DateTime endedAt) => new()
    {
        Id = id,
        ProcessorId = processorId,
        TicketId = 1,
        Status = ColumnExecutionStatus.Completed,
        TargetColumnId = targetColumnId,
        EndedAt = endedAt,
    };
}
