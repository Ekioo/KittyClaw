using KittyClaw.Core.Services;
using System.Security.Cryptography;

namespace KittyClaw.Web.Api;

public static partial class Endpoints
{
    private static void MapProjectSecrets(RouteGroupBuilder api)
    {
        api.MapGet("/projects/{slug}/secrets", async (
            string slug, ProjectService projects, ProjectSecretVault vault, CancellationToken ct) =>
        {
            if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();
            try
            {
                return Results.Ok(await vault.ListAsync(slug, ct));
            }
            catch (PlatformNotSupportedException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (ProjectSecretProtectionUnavailableException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (CryptographicException) { return VaultUnavailable(); }
            catch (InvalidDataException) { return VaultUnavailable(); }
        }).WithTags("Project secrets");

        api.MapPut("/projects/{slug}/secrets/{name}", async (
            string slug, string name, SaveProjectSecretRequest request,
            ProjectService projects, ProjectSecretVault vault, CancellationToken ct) =>
        {
            if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();
            try
            {
                await vault.SetAsync(slug, name, request.Value, ct);
                var metadata = (await vault.ListAsync(slug, ct))
                    .Single(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                return Results.Ok(metadata);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (PlatformNotSupportedException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (ProjectSecretProtectionUnavailableException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (CryptographicException) { return VaultUnavailable(); }
            catch (InvalidDataException) { return VaultUnavailable(); }
        }).WithTags("Project secrets");

        api.MapDelete("/projects/{slug}/secrets/{name}", async (
            string slug, string name, ProjectService projects, ProjectSecretVault vault, CancellationToken ct) =>
        {
            if (await projects.GetProjectAsync(slug) is null) return Results.NotFound();
            try
            {
                return await vault.DeleteAsync(slug, name, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (PlatformNotSupportedException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (ProjectSecretProtectionUnavailableException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (CryptographicException) { return VaultUnavailable(); }
            catch (InvalidDataException) { return VaultUnavailable(); }
        }).WithTags("Project secrets");
    }

    private static IResult VaultUnavailable() =>
        Results.Problem("The project secret vault could not be unlocked.", statusCode: 503);

    private sealed record SaveProjectSecretRequest(string Value);
}
