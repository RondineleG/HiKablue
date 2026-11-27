using System.Text;
using Microsoft.Extensions.Configuration;

namespace VpnControl.Security;

public static class SystemdCredentialConfiguration
{
    private const int MaxCredentialBytes = 1024 * 1024;

    public static void AddCredentials(
        IConfigurationBuilder configuration, string? credentialsDirectory)
    {
        if (string.IsNullOrWhiteSpace(credentialsDirectory))
        {
            return;
        }

        var directory = Path.GetFullPath(credentialsDirectory);
        var connectionString = ReadRequiredText(directory, "connection-string");
        var heartbeatToken = ReadRequiredText(directory, "heartbeat-token");
        var adminToken = ReadRequiredText(directory, "admin-token");
        var sessionTokenSecret = ReadRequiredText(directory, "session-token-secret");
        if (Encoding.UTF8.GetByteCount(sessionTokenSecret) < 32)
        {
            throw new InvalidOperationException(
                "systemd credential 'session-token-secret' must contain at least 32 bytes");
        }

        var serverCertificate = RequireFile(directory, "server.crt");
        var serverKey = RequireFile(directory, "server.key");
        var labCa = RequireFile(directory, "lab-ca.crt");

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VpnControl:ConnectionString"] = connectionString,
            ["VpnControl:HeartbeatToken"] = heartbeatToken,
            ["VpnControl:AdminToken"] = adminToken,
            ["VpnControl:SessionTokenSecret"] = sessionTokenSecret,
            ["Kestrel:Certificates:Default:Path"] = serverCertificate,
            ["Kestrel:Certificates:Default:KeyPath"] = serverKey,
            ["VpnControl:LabCaPath"] = labCa
        });
    }

    private static string ReadRequiredText(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' could not be read", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' could not be read", exception);
        }

        if (bytes.Length is 0 or > MaxCredentialBytes)
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' is empty or too large");
        }

        string value;
        try
        {
            value = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' is not valid UTF-8", exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' is blank");
        }

        return value;
    }

    private static string RequireFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(
                $"required systemd credential '{name}' is missing or empty");
        }

        return path;
    }
}
