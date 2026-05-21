using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class BoardLegacyManagerRemovalTests
{
    private static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string LoadBoardRazor() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Web", "Components", "Pages", "Board.razor"));

    private static string LoadEnJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.en.json"));

    private static string LoadFrJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "KittyClaw.Core", "Localization", "Board.fr.json"));

    // --- Board.razor: buttons gone ---

    [Fact]
    public void Board_DoesNotContain_OpenLabelManager_OnClick()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("OpenLabelManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_OpenMemberManager_OnClick()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("OpenMemberManager", src);
    }

    // --- Board.razor: popup @if blocks gone ---

    [Fact]
    public void Board_DoesNotContain_ShowLabelManager_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_showLabelManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_ShowMemberManager_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_showMemberManager", src);
    }

    // --- Board.razor: backing fields gone ---

    [Fact]
    public void Board_DoesNotContain_EscLabelManager_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_escLabelManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_EscMemberManager_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_escMemberManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_EditingLabelId_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_editingLabelId", src);
    }

    [Fact]
    public void Board_DoesNotContain_EditingMemberId_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_editingMemberId", src);
    }

    [Fact]
    public void Board_DoesNotContain_EditLabelName_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_editLabelName", src);
    }

    [Fact]
    public void Board_DoesNotContain_EditLabelColor_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_editLabelColor", src);
    }

    [Fact]
    public void Board_DoesNotContain_ManagerNewName_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_managerNewName", src);
    }

    [Fact]
    public void Board_DoesNotContain_ManagerNewColor_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_managerNewColor", src);
    }

    [Fact]
    public void Board_DoesNotContain_EditMemberName_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_editMemberName", src);
    }

    [Fact]
    public void Board_DoesNotContain_ManagerNewMemberName_Field()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("_managerNewMemberName", src);
    }

    // --- Board.razor: manager methods gone ---

    [Fact]
    public void Board_DoesNotContain_CloseLabelManager_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("CloseLabelManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_CloseMemberManager_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("CloseMemberManager", src);
    }

    [Fact]
    public void Board_DoesNotContain_StartEditLabel_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("StartEditLabel", src);
    }

    [Fact]
    public void Board_DoesNotContain_SaveLabel_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("SaveLabel", src);
    }

    [Fact]
    public void Board_DoesNotContain_DeleteManagedLabel_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("DeleteManagedLabel", src);
    }

    [Fact]
    public void Board_DoesNotContain_CreateManagedLabel_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("CreateManagedLabel", src);
    }

    [Fact]
    public void Board_DoesNotContain_StartEditMember_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("StartEditMember", src);
    }

    [Fact]
    public void Board_DoesNotContain_SaveMember_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("SaveMember", src);
    }

    [Fact]
    public void Board_DoesNotContain_DeleteManagedMember_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("DeleteManagedMember", src);
    }

    [Fact]
    public void Board_DoesNotContain_CreateManagedMember_Method()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotContain("CreateManagedMember", src);
    }

    // --- Board.razor: Dispose no longer references removed esc tokens ---

    [Fact]
    public void Board_Dispose_DoesNotCall_EscLabelManagerDispose()
    {
        var src = LoadBoardRazor();
        // _escLabelManager?.Dispose() must not appear anywhere (field itself is gone)
        Assert.DoesNotMatch(new Regex(@"_escLabelManager\s*\??\s*\.?\s*Dispose"), src);
    }

    [Fact]
    public void Board_Dispose_DoesNotCall_EscMemberManagerDispose()
    {
        var src = LoadBoardRazor();
        Assert.DoesNotMatch(new Regex(@"_escMemberManager\s*\??\s*\.?\s*Dispose"), src);
    }

    // --- Localization keys gone ---

    [Fact]
    public void BoardEnJson_DoesNotContain_LabelManagement_Key()
    {
        Assert.DoesNotContain("LabelManagement", LoadEnJson());
    }

    [Fact]
    public void BoardEnJson_DoesNotContain_MemberManagement_Key()
    {
        Assert.DoesNotContain("MemberManagement", LoadEnJson());
    }

    [Fact]
    public void BoardEnJson_DoesNotContain_NewLabelPlaceholder_Key()
    {
        Assert.DoesNotContain("NewLabelPlaceholder", LoadEnJson());
    }

    [Fact]
    public void BoardEnJson_DoesNotContain_NewMemberPlaceholder_Key()
    {
        Assert.DoesNotContain("NewMemberPlaceholder", LoadEnJson());
    }

    [Fact]
    public void BoardFrJson_DoesNotContain_LabelManagement_Key()
    {
        Assert.DoesNotContain("LabelManagement", LoadFrJson());
    }

    [Fact]
    public void BoardFrJson_DoesNotContain_MemberManagement_Key()
    {
        Assert.DoesNotContain("MemberManagement", LoadFrJson());
    }

    [Fact]
    public void BoardFrJson_DoesNotContain_NewLabelPlaceholder_Key()
    {
        Assert.DoesNotContain("NewLabelPlaceholder", LoadFrJson());
    }

    [Fact]
    public void BoardFrJson_DoesNotContain_NewMemberPlaceholder_Key()
    {
        Assert.DoesNotContain("NewMemberPlaceholder", LoadFrJson());
    }
}
