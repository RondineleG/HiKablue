using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VpnControl.Security;
using Xunit;

namespace VpnControl.Tests;

/// SPIFFE SAN extraction (M3 spec §2): the certificate is the node's only
/// identity; anything but an exact spiffe://lab.hikablue/node/&lt;id&gt; URI
/// is rejected.
public sealed class SpiffeCertificateValidatorTests
{
    private static X509Certificate2 MakeCert(params Uri[] sanUris)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=lab-node", key,
            HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var uri in sanUris)
        {
            san.AddUri(uri);
        }
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 MakeCertNoSan()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=lab-node", key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void ValidSpiffeUriYieldsNodeId()
    {
        using var cert = MakeCert(new Uri("spiffe://lab.hikablue/node/edge-01"));
        Assert.True(SpiffeCertificateValidator.TryGetNodeId(cert, out var nodeId));
        Assert.Equal("edge-01", nodeId);
    }

    [Fact]
    public void MissingCertificateIsRejected() =>
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(null, out _));

    [Fact]
    public void MissingSanExtensionIsRejected()
    {
        using var cert = MakeCertNoSan();
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(cert, out _));
    }

    [Fact]
    public void WrongTrustDomainIsRejected()
    {
        using var cert = MakeCert(new Uri("spiffe://other.example/node/edge-01"));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(cert, out _));
    }

    [Fact]
    public void WildcardOrEmptyNodeIdIsRejected()
    {
        using var star = MakeCert(new Uri("spiffe://lab.hikablue/node/*"));
        using var bare = MakeCert(new Uri("spiffe://lab.hikablue/node/"));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(star, out _));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(bare, out _));
    }

    [Fact]
    public void UppercaseOrOverlongNodeIdIsRejected()
    {
        using var upper = MakeCert(new Uri("spiffe://lab.hikablue/node/Edge-01"));
        using var long_ = MakeCert(new Uri(
            "spiffe://lab.hikablue/node/" + new string('a', 64)));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(upper, out _));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(long_, out _));
    }

    [Fact]
    public void DnsNameOnlySanIsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=lab-node", key,
            HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("edge-01.lab.hikablue");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(SpiffeCertificateValidator.TryGetNodeId(cert, out _));
    }
}
