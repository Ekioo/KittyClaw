using KittyClaw.Core.Automation;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Tests.Automation;

public sealed class ColumnAgentDispatcherTests
{
    [Fact]
    public void Runtime_contract_reserves_ticket_routing_and_uses_canonical_memory_path()
    {
        var processor = new ColumnProcessor
        {
            Id = 4,
            ColumnId = 19,
            Name = "Video producer",
        };

        var contract = ColumnAgentDispatcher.BuildRuntimeContract(processor);

        Assert.Contains("Never PATCH or otherwise change the current ticket's pipeline, column, status, or assignedTo", contract);
        Assert.Contains("final JSON outcome", contract);
        Assert.Contains(".agents/processors/column-19/memory/MEMORY.md", contract);
        Assert.Contains("never create or use `.agents/column-19/memory.md`", contract);
    }
}
