using System.Security.Cryptography;
using System.Text;

namespace GlideScroll.Licensing;

/// <summary>
/// Receipt-style license check. Reads a signed license file from disk and
/// verifies its signature against an RSA public key embedded in the binary.
/// If valid, paid features are enabled. No network, no phone-home, no expiry,
/// no trial timer. It is crackable by design: this is a receipt, not a lock.
/// The point is a clean signed proof of purchase, not copy protection.
///
/// License file (optional; absence just means unlicensed):
///   %LOCALAPPDATA%\GlideScroll\license.key
/// Format, UTF-8 text, two lines:
///   line 1: base64( payload-json )   e.g. {"edition":"pro","licensee":"Jane","issued":"2026-07-12"}
///   line 2: base64( RSA-SHA256 signature over the raw payload-json bytes )
///
/// Signing is done offline by the vendor with the PRIVATE key (which never
/// ships). Example (a one-off tool, not part of this app):
///   var payload = Encoding.UTF8.GetBytes(json);
///   var sig = privateRsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
///   File.WriteAllText(path, Convert.ToBase64String(payload) + "\n" + Convert.ToBase64String(sig));
///
/// To activate: generate an RSA-2048 keypair once, paste the PUBLIC key
/// (SubjectPublicKeyInfo, base64 DER) into <see cref="PublicKeySpkiBase64"/>,
/// and keep the private key off every shipped machine.
/// </summary>
internal static class LicenseVerifier
{
    // Vendor RSA-2048 public key, SubjectPublicKeyInfo as base64 DER.
    // Placeholder: until this is a real key, every license verifies as
    // unlicensed (the import throws and is caught). Nothing is unlocked by
    // accident.
    private const string PublicKeySpkiBase64 = "REPLACE_WITH_VENDOR_RSA2048_PUBLIC_KEY_SPKI_BASE64";

    public static LicenseStatus Check()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlideScroll", "license.key");
            if (!File.Exists(path)) return LicenseStatus.Unlicensed;

            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) return LicenseStatus.Unlicensed;

            byte[] payload = Convert.FromBase64String(lines[0].Trim());
            byte[] signature = Convert.FromBase64String(lines[1].Trim());

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpkiBase64), out _);

            bool valid = rsa.VerifyData(
                payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!valid) return LicenseStatus.Unlicensed;

            return new LicenseStatus(true, Encoding.UTF8.GetString(payload));
        }
        catch
        {
            // Any malformed file, missing/placeholder key, or bad signature:
            // fail closed to unlicensed. Never throw into startup.
            return LicenseStatus.Unlicensed;
        }
    }
}

/// <summary>Result of a license check. PayloadJson is the verified receipt
/// contents (edition, licensee, and so on) when Pro, otherwise null.</summary>
internal sealed class LicenseStatus
{
    public bool IsPro { get; }
    public string? PayloadJson { get; }

    public LicenseStatus(bool isPro, string? payloadJson = null)
    {
        IsPro = isPro;
        PayloadJson = payloadJson;
    }

    public static readonly LicenseStatus Unlicensed = new(false);
}

/// <summary>App-wide licensing state, resolved once at startup. Future paid
/// features gate on <see cref="IsPro"/>. There are no paid features yet, so this
/// is plumbing only.</summary>
internal static class License
{
    public static bool IsPro { get; private set; }
    public static string? PayloadJson { get; private set; }

    public static void Initialize()
    {
        var status = LicenseVerifier.Check();
        IsPro = status.IsPro;
        PayloadJson = status.PayloadJson;
    }
}
