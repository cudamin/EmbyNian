namespace EmbyNian.Configuration;

/// <summary>
/// Wraps and unwraps values that must not sit in plain text inside settings.json.
/// The real implementation is DPAPI (per-Windows-user); tests use
/// <see cref="PassthroughSecretProtector"/>.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string Unprotect(string cipherText);
}

public sealed class PassthroughSecretProtector : ISecretProtector
{
    public static readonly PassthroughSecretProtector Instance = new();

    public string Protect(string plainText) => plainText;

    public string Unprotect(string cipherText) => cipherText;
}
