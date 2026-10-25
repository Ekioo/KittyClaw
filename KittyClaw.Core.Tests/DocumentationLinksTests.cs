using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KittyClaw.Core.Tests;

public sealed class DocumentationLinksTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KittyClaw.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void ArchitectureIndex_UsesTheUnifiedDocumentationDirectory()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "docs", "index.md")));
        Assert.False(File.Exists(Path.Combine(root, "doc", "index.md")));
    }

    [Fact]
    public void DocumentationRelativeLinks_ResolveToExistingFiles()
    {
        var root = RepositoryRoot();
        var documents = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat([Path.Combine(root, "README.md"), Path.Combine(root, "AGENTS.md")]);
        var missing = new List<string>();
        foreach (var file in documents)
        {
            var document = Markdown.Parse(File.ReadAllText(file));
            foreach (var link in document.Descendants<LinkInline>())
            {
                var url = link.Url;
                if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.StartsWith('/')
                    || Uri.TryCreate(url, UriKind.Absolute, out _)) continue;
                var path = Uri.UnescapeDataString(url.Split('#', '?')[0]);
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path));
                if (!File.Exists(target) && !Directory.Exists(target))
                    missing.Add($"{Path.GetRelativePath(root, file)}: {url}");
            }
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }
}
