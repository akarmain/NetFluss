// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using System.Xml;

namespace NetFluss.Core;

/// <summary>Outcome of building a Windows WLAN profile for a network being joined.</summary>
public sealed record WifiProfileResult(string? Xml, string? Error)
{
    public bool IsValid => Xml is not null;
}

/// <summary>
/// Builds the WLAN profile XML Windows needs to join a network it has not seen before.
///
/// <para>On macOS a successful join is written into Known Networks by the privileged
/// helper, so the system menu reuses the password later. The Windows equivalent is a
/// profile: once written, the Windows Wi-Fi flyout knows the network too, with no helper
/// and no elevation.</para>
///
/// <para>The document is produced with <see cref="XmlWriter"/> rather than string
/// concatenation because the passphrase is user-typed and lands verbatim inside it. A
/// password containing <c>&lt;/keyMaterial&gt;</c> must stay a password.</para>
/// </summary>
public static class WifiProfile
{
    private const string Namespace = "http://www.microsoft.com/networking/WLAN/profile/v1";

    public static WifiProfileResult Build(string ssid, uint authAlgorithm, uint cipherAlgorithm, string? password)
    {
        if (string.IsNullOrEmpty(ssid) || Encoding.UTF8.GetByteCount(ssid) > 32)
        {
            return new WifiProfileResult(null, "That network name cannot be used.");
        }

        var (authentication, encryption, needsKey) = Map(authAlgorithm, cipherAlgorithm);
        if (authentication is null)
        {
            return new WifiProfileResult(null, "Enterprise networks need to be joined from Windows Settings.");
        }

        string? keyType = null;
        if (needsKey)
        {
            if (string.IsNullOrEmpty(password))
            {
                return new WifiProfileResult(null, "Enter the network password.");
            }

            if (encryption == "WEP")
            {
                // 5 or 13 ASCII characters, or 10 or 26 hex digits.
                keyType = "networkKey";
                if (password.Length is not (5 or 13 or 10 or 26))
                {
                    return new WifiProfileResult(null, "A WEP key is 5 or 13 characters, or 10 or 26 hex digits.");
                }
            }
            else if (password.Length == 64 && password.All(Uri.IsHexDigit))
            {
                keyType = "networkKey";
            }
            else
            {
                keyType = "passPhrase";
                if (password.Length is < 8 or > 63)
                {
                    return new WifiProfileResult(null, "Wi-Fi passwords are 8 to 63 characters long.");
                }
            }
        }

        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = Encoding.Unicode,
        };

        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("WLANProfile", Namespace);
            writer.WriteElementString("name", Namespace, ssid);

            writer.WriteStartElement("SSIDConfig", Namespace);
            writer.WriteStartElement("SSID", Namespace);
            writer.WriteElementString("hex", Namespace, Convert.ToHexString(Encoding.UTF8.GetBytes(ssid)));
            writer.WriteElementString("name", Namespace, ssid);
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteElementString("connectionType", Namespace, "ESS");
            writer.WriteElementString("connectionMode", Namespace, "auto");

            writer.WriteStartElement("MSM", Namespace);
            writer.WriteStartElement("security", Namespace);

            writer.WriteStartElement("authEncryption", Namespace);
            writer.WriteElementString("authentication", Namespace, authentication);
            writer.WriteElementString("encryption", Namespace, encryption);
            writer.WriteElementString("useOneX", Namespace, "false");
            writer.WriteEndElement();

            if (needsKey)
            {
                writer.WriteStartElement("sharedKey", Namespace);
                writer.WriteElementString("keyType", Namespace, keyType);
                writer.WriteElementString("protected", Namespace, "false");
                writer.WriteElementString("keyMaterial", Namespace, password);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return new WifiProfileResult(builder.ToString(), null);
    }

    /// <summary>
    /// DOT11_AUTH_ALGORITHM / DOT11_CIPHER_ALGORITHM to profile vocabulary. A null
    /// authentication means the network needs 802.1X configuration this app does not offer.
    /// </summary>
    private static (string? Authentication, string Encryption, bool NeedsKey) Map(uint auth, uint cipher)
    {
        var isWep = cipher is 0x01 or 0x05 or 0x101;
        var isTkip = cipher == 0x02;

        return auth switch
        {
            1 when isWep => ("open", "WEP", true),
            1 => ("open", "none", false),
            2 => ("shared", "WEP", true),
            4 => ("WPAPSK", isTkip ? "TKIP" : "AES", true),
            7 => ("WPA2PSK", isTkip ? "TKIP" : "AES", true),
            9 => ("WPA3SAE", "AES", true),
            10 => ("OWE", "AES", false),
            _ => (null, string.Empty, false),
        };
    }
}
