using RecruIT.List.Hosting;

namespace RecruIT.List.Tests;

public sealed class IsLocalUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/trials/abc/patients")]
    [InlineData("/path?x=https://other.example")]
    public void Accepts_paths_on_this_site(string url) =>
        Assert.True(AuthenticationExtensions.IsLocalUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_everything_that_could_leave_the_site(string? url) =>
        Assert.False(AuthenticationExtensions.IsLocalUrl(url));
}
