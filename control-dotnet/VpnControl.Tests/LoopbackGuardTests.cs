using VpnControl.Models;
using VpnControl.Services;
using VpnControl.Security;
using Xunit;

namespace VpnControl.Tests;

public class LoopbackGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("http://127.0.0.2:5080")] // any 127.x is loopback
    [InlineData("http://127.0.0.1:5080;http://[::1]:5080")]
    public void Loopback_urls_are_accepted(string urls)
    {
        Assert.Null(LoopbackGuard.FindNonLoopback(urls));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080")]       // every interface
    [InlineData("http://+:5080")]             // wildcard
    [InlineData("http://*:5080")]             // wildcard
    [InlineData("http://[::]:5080")]          // every v6 interface
    [InlineData("http://192.168.1.10:5080")]  // LAN address
    [InlineData("http://127.0.0.1:5080;http://0.0.0.0:5080")] // one bad spoils all
    [InlineData("not-a-url")]                 // unparseable: refuse
    public void Non_loopback_urls_are_rejected(string urls)
    {
        Assert.NotNull(LoopbackGuard.FindNonLoopback(urls));
    }
}
