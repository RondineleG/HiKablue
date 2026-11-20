using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace VpnControl.Security;

/// <summary>
/// Extracts and validates the node identity carried in a client
/// certificate's Subject Alternative Name (OID 2.5.29.17) as a SPIFFE URI:
/// spiffe://lab.hikablue/node/&lt;node-id&gt;. One certificate, one node —
/// the SAN URI is the only trusted identity source (M3 spec §2).
/// </summary>
public static partial class SpiffeCertificateValidator
{
    // Context-specific tag 6 inside the SAN GeneralName sequence =
    // uniformResourceIdentifier (IA5String, implicit).
    private const int UniformResourceIdentifierTag = 6;
    private const string SanOid = "2.5.29.17";

    [GeneratedRegex(@"^spiffe://lab\.hikablue/node/([a-z0-9\-]{1,63})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdPattern();

    /// <summary>
    /// Returns the SPIFFE node id when the certificate carries exactly the
    /// expected SAN URI shape; otherwise false. Wildcards, other trust
    /// domains, DNS names or a missing SAN are all rejected.
    /// </summary>
    public static bool TryGetNodeId(X509Certificate2? certificate, out string nodeId)
    {
        nodeId = "";
        if (certificate is null)
        {
            return false;
        }

        var san = certificate.Extensions[SanOid];
        if (san is null)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
            var generalNames = reader.ReadSequence();
            while (generalNames.HasData)
            {
                var tag = generalNames.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific &&
                    tag.TagValue == UniformResourceIdentifierTag)
                {
                    var uri = generalNames.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, UniformResourceIdentifierTag));
                    var match = NodeIdPattern().Match(uri);
                    if (match.Success)
                    {
                        nodeId = match.Groups[1].Value;
                        return true;
                    }
                }
                else
                {
                    generalNames.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            return false;
        }

        return false;
    }
}
