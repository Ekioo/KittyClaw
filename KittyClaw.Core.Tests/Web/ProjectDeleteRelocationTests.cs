using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class ProjectDeleteRelocationTests
{
    private static string FindRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "KittyClaw.sln"))
                               && !File.Exists(Path.Combine(dir, "KittyClaw.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return dir!;
    }

    private static string LoadHomeRazor() =>
        File.ReadAllText(Path.Combine(FindRoot(), "KittyClaw.Web", "Components", "Pages", "Home.razor"));

    private static string LoadProjectSettingsRazor() =>
        File.ReadAllText(Path.Combine(FindRoot(), "KittyClaw.Web", "Components", "Pages", "ProjectSettings.razor"));

    // Case: delete button must be removed from Home cards
    [Fact]
    public void Home_DoesNotContain_ProjectDeleteBtn()
    {
        var src = LoadHomeRazor();
        Assert.DoesNotContain("project-delete-btn", src);
    }

    // Case: delete modal (_deleteTarget) must be removed from Home
    [Fact]
    public void Home_DoesNotContain_DeleteTargetField()
    {
        var src = LoadHomeRazor();
        Assert.DoesNotContain("_deleteTarget", src);
    }

    // Case: AskDelete / ConfirmDelete / CancelDelete methods must be removed from Home
    [Fact]
    public void Home_DoesNotContain_DeleteMethods()
    {
        var src = LoadHomeRazor();
        Assert.DoesNotContain("AskDelete", src);
        Assert.DoesNotContain("ConfirmDelete", src);
        Assert.DoesNotContain("CancelDelete", src);
    }

    // Case: delete button present in Settings (danger zone)
    [Fact]
    public void ProjectSettings_Contains_DeleteProjectButton()
    {
        var src = LoadProjectSettingsRazor();
        // The button should carry a red/danger styling or be inside a danger-zone section
        // and call DeleteProjectAsync (or set a confirm state)
        Assert.Matches(new Regex(@"danger.zone|DangerZone|danger-zone", RegexOptions.IgnoreCase), src);
    }

    // Case: confirmation state field exists in Settings
    [Fact]
    public void ProjectSettings_Contains_DeleteProjectConfirmField()
    {
        var src = LoadProjectSettingsRazor();
        Assert.Matches(new Regex(@"_deleteConfirmProject|_showDeleteProject|_confirmDeleteProject"), src);
    }

    // Case: confirmation UI block exists (cancel + confirm buttons)
    [Fact]
    public void ProjectSettings_Contains_DeleteProjectConfirmBlock()
    {
        var src = LoadProjectSettingsRazor();
        // Confirm block must reference the confirm field and have both cancel and delete actions
        Assert.Matches(new Regex(@"_deleteConfirmProject|_showDeleteProject|_confirmDeleteProject"), src);
        // cancel btn sets confirm field back to false/null
        Assert.Matches(new Regex(@"cancel-btn"), src);
    }

    // Case: clicking confirm calls DeleteProjectAsync and navigates away
    [Fact]
    public void ProjectSettings_DeleteMethod_CallsDeleteProjectAsyncAndNavigates()
    {
        var src = LoadProjectSettingsRazor();
        Assert.Contains("DeleteProjectAsync", src);
        Assert.Matches(new Regex(@"Nav\.NavigateTo\s*\(\s*""/"""), src);
    }

    // Case: exception guard — try/catch around delete call
    [Fact]
    public void ProjectSettings_DeleteMethod_HasTryCatch()
    {
        var src = LoadProjectSettingsRazor();
        // The delete project method should be wrapped in try/catch
        // Simplest: count try blocks in the settings file — after impl there must be at least one
        // more specifically tied to DeleteProjectAsync context.
        // We assert try appears near DeleteProjectAsync with a loose regex spanning lines.
        Assert.Matches(new Regex(@"try[\s\S]{0,300}DeleteProjectAsync|DeleteProjectAsync[\s\S]{0,300}catch", RegexOptions.Singleline), src);
    }

    // Regression: pause button must still be in Home (not accidentally removed)
    [Fact]
    public void Home_StillContains_PauseButton()
    {
        var src = LoadHomeRazor();
        Assert.Contains("project-pause-btn", src);
    }
}
