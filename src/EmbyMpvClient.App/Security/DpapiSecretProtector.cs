using System.Security.Cryptography;
using System.Text;
using EmbyMpvClient.Configuration;

namespace EmbyMpvClient.App.Security;

/// <summary>
/// Wraps secrets with the Windows Data Protection API, tied to the current user account.
/// A copied settings.json is useless on another machine or under another login.
/// <para>
/// This lives in the WinForms project on purpose: the ProtectedData assembly ships only with
/// the Windows Desktop framework, so keeping it out of Core is what lets Core (and the test
/// runner) stay a plain, portable library with no package references.
/// </para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Additional entropy. This exact string was used by v1, and DPAPI will not decrypt
    /// without the same value — changing it would silently invalidate every saved password
    /// on upgrade. Do not touch it.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EmbyMpvClient.Profile.v1");

    public static readonly DpapiSecretProtector Instance = new();

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public string Unprotect(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";

        var plain = ProtectedData.Unprotect(Convert.FromBase64String(cipherText), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
