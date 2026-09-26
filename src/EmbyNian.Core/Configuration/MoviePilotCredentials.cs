using EmbyNian.Diagnostics;

namespace EmbyNian.Configuration;

/// <summary>
/// The one place the MoviePilot password is wrapped and unwrapped, for the same reason
/// <see cref="CredentialVault"/> exists for the Emby ones: callers deal in plain strings and never see the
/// wrapped form, so no view has to know how protection works.
/// <para>
/// Separate from <see cref="CredentialVault"/> rather than a third method on it because that class takes an
/// <see cref="AccountProfile"/> — a concept MoviePilot does not have. Two small classes over one that has to
/// accept two unrelated shapes.
/// </para>
/// </summary>
public sealed class MoviePilotCredentials(ISecretProtector protector)
{
    private const string Category = "credentials";

    public string GetPassword(MoviePilotSettings settings) => Unwrap(settings.ProtectedPassword);

    internal string GetPassword(string protectedPassword) => Unwrap(protectedPassword);

    public void SetPassword(MoviePilotSettings settings, string password) =>
        settings.ProtectedPassword = string.IsNullOrEmpty(password) ? "" : Wrap(password);

    public void ClearPassword(MoviePilotSettings settings) => settings.ProtectedPassword = "";

    private string Wrap(string value)
    {
        try
        {
            return protector.Protect(value);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "无法加密 MoviePilot 凭据，将不保存", error);
            return "";
        }
    }

    private string Unwrap(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return protector.Unprotect(value);
        }
        catch (Exception error)
        {
            // Happens when settings.json was copied from another Windows account.
            Log.Warn(Category, "无法解密已保存的 MoviePilot 凭据，需要重新输入", error);
            return "";
        }
    }
}
