using Microsoft.Extensions.Configuration;
using VpnControl.Security;
using Xunit;

namespace VpnControl.Tests;

/// The heartbeat secret must fail closed: no configured token means no
/// service, and only an exact token match passes the header check.
public class HeartbeatAuthTests
{
    private static IConfiguration ConfigWith(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VpnControl:HeartbeatToken"] = value })
            .Build();

    [Fact]
    public void RequireTokenThrowsWhenUnset() =>
        Assert.Throws<InvalidOperationException>(
            () => HeartbeatAuth.RequireToken(ConfigWith(null)));

    [Fact]
    public void RequireTokenThrowsWhenBlank() =>
        Assert.Throws<InvalidOperationException>(
            () => HeartbeatAuth.RequireToken(ConfigWith("   ")));

    [Fact]
    public void RequireTokenReturnsTheConfiguredValue() =>
        Assert.Equal("s3cret", HeartbeatAuth.RequireToken(ConfigWith("s3cret")));

    [Theory]
    [InlineData("s3cret", true)]
    [InlineData("s3cre", false)]
    [InlineData("s3cret2", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void MatchesRequiresAnExactToken(string? provided, bool expected) =>
        Assert.Equal(expected, HeartbeatAuth.Matches(provided, "s3cret"));
}
