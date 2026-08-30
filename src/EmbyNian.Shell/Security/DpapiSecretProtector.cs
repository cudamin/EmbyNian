using System.Security.Cryptography;
using System.Text;
using EmbyNian.Configuration;

namespace EmbyNian.Shell.Security;

/// <summary>
/// Wraps secrets with the Windows Data Protection API, tied to the current user account.
/// A copied settings.json is useless on another machine or under another login.
/// <para>
/// Kept in the shell rather than Core because Core has no package references and ProtectedData is not
/// part of its target framework. The shell owns the platform-specific credential implementation.
/// </para>
/// </summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Additional entropy. This exact string was used by v1, and DPAPI will not decrypt without the
    /// same value — changing it would silently invalidate every saved password on upgrade. It
    /// deliberately keeps the old product name through the EmbyNian rename: it is a versioned key
    /// label, not a display name. Do not touch it, and do not "fix" it to match this project's name.
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
