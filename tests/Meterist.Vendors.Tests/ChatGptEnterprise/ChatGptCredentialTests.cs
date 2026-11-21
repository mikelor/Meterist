using Meterist.Vendors.ChatGptEnterprise;

namespace Meterist.Vendors.Tests.ChatGptEnterprise;

public class ChatGptCredentialTests
{
    private const string ValidWorkspaceId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void TryParse_WithoutWorkspaceId_SucceedsWithNullWorkspaceId()
    {
        // The shape every credential had before WorkspaceId existed: it must
        // keep working so COSTS extraction is unaffected.
        const string json = """{"OrganizationId":"org-abc123","AdminApiKey":"admin-key-xyz"}""";

        var ok = ChatGptCredential.TryParse(json, out var credential, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("org-abc123", credential!.OrganizationId);
        Assert.Equal("admin-key-xyz", credential.AdminApiKey);
        Assert.Null(credential.WorkspaceId);
    }

    [Fact]
    public void TryParse_WithWorkspaceId_ReadsIt()
    {
        var json = $$"""{"OrganizationId":"org-abc123","AdminApiKey":"admin-key-xyz","WorkspaceId":"{{ValidWorkspaceId}}"}""";

        var ok = ChatGptCredential.TryParse(json, out var credential, out _);

        Assert.True(ok);
        Assert.Equal(ValidWorkspaceId, credential!.WorkspaceId);
    }

    [Theory]
    [InlineData("org-ExamplePlaceholder0000")]   // an organization ID (org-...) pasted where the workspace UUID belongs
    [InlineData("not-a-uuid")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_WithWorkspaceIdThatIsNotAUuid_FailsAndExplainsTheOrganizationIdMixUp(string workspaceId)
    {
        var json = $$"""{"OrganizationId":"org-abc123","AdminApiKey":"admin-key-xyz","WorkspaceId":"{{workspaceId}}"}""";

        var ok = ChatGptCredential.TryParse(json, out var credential, out var error);

        Assert.False(ok);
        Assert.Null(credential);
        Assert.Contains("WorkspaceId", error);
        Assert.Contains("Organization ID", error);
    }

    [Theory]
    [InlineData("""{"AdminApiKey":"admin-key-xyz"}""")]
    [InlineData("""{"OrganizationId":"org-abc123"}""")]
    [InlineData("""{"OrganizationId":"","AdminApiKey":"admin-key-xyz"}""")]
    [InlineData("""{"OrganizationId":"org-abc123","AdminApiKey":"  "}""")]
    [InlineData("not json at all")]
    [InlineData("null")]
    public void TryParse_WithMissingOrEmptyRequiredFieldsOrBadJson_Fails(string json)
    {
        var ok = ChatGptCredential.TryParse(json, out var credential, out var error);

        Assert.False(ok);
        Assert.Null(credential);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_Failure_NeverEchoesTheAdminKey()
    {
        const string secret = "sk-admin-SECRET-VALUE";
        var json = $$"""{"OrganizationId":"org-abc123","AdminApiKey":"{{secret}}","WorkspaceId":"org-wrong"}""";

        ChatGptCredential.TryParse(json, out _, out var error);

        Assert.DoesNotContain(secret, error);
    }
}
