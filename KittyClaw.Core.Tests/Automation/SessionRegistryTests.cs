using KittyClaw.Core.Automation;

namespace KittyClaw.Core.Tests.Automation;

public sealed class SessionRegistryTests
{
    [Fact]
    public void WriteAllTextWithRetry_RetriesTransientIoFailures()
    {
        var writes = 0;
        var delays = new List<TimeSpan>();

        SessionRegistry.WriteAllTextWithRetry(
            "dispatch-state.json",
            "{}",
            (_, _) =>
            {
                writes++;
                if (writes < 3)
                    throw new IOException("sharing violation");
            },
            delays.Add);

        Assert.Equal(3, writes);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50)],
            delays);
    }

    [Fact]
    public void WriteAllTextWithRetry_DoesNotRetryNonIoFailures()
    {
        var writes = 0;
        var delays = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            SessionRegistry.WriteAllTextWithRetry(
                "dispatch-state.json",
                "{}",
                (_, _) =>
                {
                    writes++;
                    throw new UnauthorizedAccessException();
                },
                _ => delays++));

        Assert.Equal(1, writes);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void WriteAllTextWithRetry_SurfacesPersistentIoFailureAfterBoundedRetries()
    {
        var writes = 0;
        var delays = 0;

        Assert.Throws<IOException>(() =>
            SessionRegistry.WriteAllTextWithRetry(
                "dispatch-state.json",
                "{}",
                (_, _) =>
                {
                    writes++;
                    throw new IOException("still locked");
                },
                _ => delays++));

        Assert.Equal(6, writes);
        Assert.Equal(5, delays);
    }
}
