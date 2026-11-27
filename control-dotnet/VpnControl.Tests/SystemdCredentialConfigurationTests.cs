using Microsoft.Extensions.Configuration;
using VpnControl.Security;
using Xunit;

namespace VpnControl.Tests;

public sealed class SystemdCredentialConfigurationTests
{
    [Fact]
    public void AddCredentialsMapsSecretsAndRuntimeCertificatePaths()
    {
        var directory = CreateCredentialDirectory();
        try
        {
            Write(directory, "connection-string", "Host=db;Database=vpn\n");
            Write(directory, "heartbeat-token", "heartbeat\n");
            Write(directory, "admin-token", "admin\n");
            var sessionTokenSecret = new string('\u00e9', 16);
            Write(directory, "session-token-secret", sessionTokenSecret);
            Write(directory, "server.crt", "certificate");
            Write(directory, "server.key", "private-key");
            Write(directory, "lab-ca.crt", "lab-ca");
            var builder = new ConfigurationBuilder();

            SystemdCredentialConfiguration.AddCredentials(builder, directory);

            var configuration = builder.Build();
            Assert.Equal("Host=db;Database=vpn",
                configuration["VpnControl:ConnectionString"]);
            Assert.Equal("heartbeat", configuration["VpnControl:HeartbeatToken"]);
            Assert.Equal("admin", configuration["VpnControl:AdminToken"]);
            Assert.Equal(sessionTokenSecret, configuration["VpnControl:SessionTokenSecret"]);
            Assert.Equal(Path.Combine(directory, "server.crt"),
                configuration["Kestrel:Certificates:Default:Path"]);
            Assert.Equal(Path.Combine(directory, "server.key"),
                configuration["Kestrel:Certificates:Default:KeyPath"]);
            Assert.Equal(Path.Combine(directory, "lab-ca.crt"),
                configuration["VpnControl:LabCaPath"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddCredentialsFailsClosedWhenARequiredCredentialIsMissing()
    {
        var directory = CreateCredentialDirectory();
        try
        {
            Write(directory, "heartbeat-token", "heartbeat");
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SystemdCredentialConfiguration.AddCredentials(
                    new ConfigurationBuilder(), directory));

            Assert.Contains("connection-string", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddCredentialsRejectsAnUndersizedSessionTokenSecret()
    {
        var directory = CreateCredentialDirectory();
        try
        {
            Write(directory, "connection-string", "Host=db");
            Write(directory, "heartbeat-token", "heartbeat");
            Write(directory, "admin-token", "admin");
            Write(directory, "session-token-secret", "too-short");
            Write(directory, "server.crt", "certificate");
            Write(directory, "server.key", "private-key");
            Write(directory, "lab-ca.crt", "lab-ca");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                SystemdCredentialConfiguration.AddCredentials(
                    new ConfigurationBuilder(), directory));

            Assert.Contains("session-token-secret", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateCredentialDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hikablue-creds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Write(string directory, string name, string value) =>
        File.WriteAllText(Path.Combine(directory, name), value);
}
