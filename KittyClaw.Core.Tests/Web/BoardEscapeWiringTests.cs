using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class BoardEscapeWiringTests
{
    private static string LoadBoardRazor()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "KittyClaw.Web", "Components", "Pages", "Board.razor");
        Assert.True(File.Exists(path), $"Board.razor not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void LabelManager_EscapeToken_IsAssignedOnOpen()
    {
        var src = LoadBoardRazor();
        Assert.Matches(new Regex(@"_escLabelManager\s*=\s*EscapeStack\.PushWithFocus\("), src);
    }

    [Fact]
    public void MemberManager_EscapeToken_IsAssignedOnOpen()
    {
        var src = LoadBoardRazor();
        Assert.Matches(new Regex(@"_escMemberManager\s*=\s*EscapeStack\.PushWithFocus\("), src);
    }

    [Fact]
    public void LabelManager_EscapeToken_DisposedOnClose()
    {
        var src = LoadBoardRazor();
        Assert.Matches(new Regex(@"_escLabelManager\s*=\s*null"), src);
    }

    [Fact]
    public void MemberManager_EscapeToken_DisposedOnClose()
    {
        var src = LoadBoardRazor();
        Assert.Matches(new Regex(@"_escMemberManager\s*=\s*null"), src);
    }
}
