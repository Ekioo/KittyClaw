using System.Security.Cryptography;
using System.Text;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Core.Tests.Services;
using Xunit.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class RepositoryStateCaptureSecurityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Large_untracked_file_is_hashed_without_allocating_its_full_contents()
    {
        using var temp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        // Warm the Git and crypto paths before measuring the actual large-file capture.
        await File.WriteAllTextAsync(Path.Combine(repository, "warmup.txt"), "warmup");
        await AgentRunner.CaptureRepositoryStateAsync(repository, CancellationToken.None);
        var path = Path.Combine(repository, "large.bin");
        await using (var file = File.Create(path)) file.SetLength(64L * 1024 * 1024);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var state = await AgentRunner.CaptureRepositoryStateAsync(repository, CancellationToken.None);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        output.WriteLine($"Managed allocation for a 64 MiB untracked file: {allocated:N0} bytes.");
        Assert.Contains("\0large.bin:", state);
        Assert.True(state.Length < 4096, "The fingerprint retained file contents instead of hashes.");
        Assert.True(allocated < 32L * 1024 * 1024,
            $"Capturing a 64 MiB untracked file allocated {allocated:N0} managed bytes (budget: 32 MiB).");
    }

    [Fact]
    public async Task Same_length_untracked_edit_preserves_exact_hashes_and_path_attribution()
    {
        using var temp = new TempDir();
        var repository = ProjectWorktreeSettingsTests.CreateRepository(temp.Path, "main");
        var path = Path.Combine(repository, "content.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await AgentRunner.CaptureRepositoryStateAsync(repository, CancellationToken.None);
        Assert.Contains("\0content.txt:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("before"))), before);
        Assert.Equal(before, await AgentRunner.CaptureRepositoryStateAsync(repository, CancellationToken.None));

        await File.WriteAllTextAsync(path, "edited");
        var after = await AgentRunner.CaptureRepositoryStateAsync(repository, CancellationToken.None);
        Assert.Contains("\0content.txt:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("edited"))), after);
        Assert.Equal(["content.txt"], PrimaryCheckoutChangeAttribution.ChangedPaths(before, after));
    }

    [Fact]
    public async Task Cancelled_capture_does_not_return_a_partial_fingerprint()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentRunner.CaptureRepositoryStateAsync(
            Path.GetTempPath(), cancellation.Token));
    }
}
