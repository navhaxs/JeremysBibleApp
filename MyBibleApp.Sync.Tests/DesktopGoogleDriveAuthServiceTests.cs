using System;
using System.IO;
using System.Threading.Tasks;
using MyBibleApp.Services.Sync;
using Xunit;

namespace MyBibleApp.Sync.Tests;

public sealed class DesktopGoogleDriveAuthServiceTests
{
    [Fact]
    public void Constructor_WithFilePath_DoesNotThrow()
    {
        var service = new DesktopGoogleDriveAuthService("nonexistent.json");
        Assert.False(service.IsAuthenticated);
        Assert.Null(service.CurrentAccessToken);
        Assert.Null(service.CurrentUserEmail);
    }

    [Fact]
    public async Task AuthenticateAsync_MissingCredentialsFile_ReturnsFailure()
    {
        var service = new DesktopGoogleDriveAuthService(
            Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json"));

        var result = await service.AuthenticateAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("Credentials file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidStream_ReturnsFailure()
    {
        // Provide a stream factory that returns invalid JSON — should fail gracefully
        var service = new DesktopGoogleDriveAuthService(
            () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not valid json")));

        var result = await service.AuthenticateAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        // ValidateDesktopCredentials returns a specific message for invalid JSON
        // (does not go through the generic "Authentication failed" catch path).
        Assert.True(
            result.ErrorMessage!.Contains("Authentication failed") ||
            result.ErrorMessage.Contains("Credentials JSON is invalid"),
            $"Unexpected error message: {result.ErrorMessage}");
    }

    [Fact]
    public void Constructor_WithStreamFactory_DoesNotThrow()
    {
        var service = new DesktopGoogleDriveAuthService(
            () => new MemoryStream(Array.Empty<byte>()));

        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void GetDriveService_WhenNotAuthenticated_ReturnsNull()
    {
        var service = new DesktopGoogleDriveAuthService("nonexistent.json");
        Assert.Null(service.GetDriveService());
    }

    [Fact]
    public async Task RefreshToken_WhenNotAuthenticated_ReturnsFalse()
    {
        var service = new DesktopGoogleDriveAuthService("nonexistent.json");
        var result = await service.RefreshTokenAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task Revoke_WhenNotAuthenticated_ReturnsTrue()
    {
        var service = new DesktopGoogleDriveAuthService("nonexistent.json");
        var result = await service.RevokeAsync();
        Assert.True(result);
    }
}
