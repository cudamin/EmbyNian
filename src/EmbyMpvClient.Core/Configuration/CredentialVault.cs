using EmbyMpvClient.Diagnostics;

namespace EmbyMpvClient.Configuration;

/// <summary>
/// The only place that unwraps stored secrets. Callers deal in plain strings and
/// never see the wrapped form, so no view has to know how protection works.
/// </summary>
public sealed class CredentialVault(ISecretProtector protector)
{
    private const string Category = "credentials";

    public string GetPassword(AccountProfile account) => Unwrap(account.ProtectedPassword);

    public void SetPassword(AccountProfile account, string password, bool remember)
    {
        account.RememberPassword = remember;
        account.ProtectedPassword = remember && !string.IsNullOrEmpty(password) ? Wrap(password) : "";
    }

    public string GetAccessToken(AccountProfile account) => Unwrap(account.ProtectedAccessToken);

    public void SetAccessToken(AccountProfile account, string token) =>
        account.ProtectedAccessToken = string.IsNullOrEmpty(token) ? "" : Wrap(token);

    public void ClearAccessToken(AccountProfile account) => account.ProtectedAccessToken = "";

    private string Wrap(string value)
    {
        try
        {
            return protector.Protect(value);
        }
        catch (Exception error)
        {
            Log.Warn(Category, "无法加密凭据，将不保存", error);
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
            Log.Warn(Category, "无法解密已保存的凭据，需要重新输入", error);
            return "";
        }
    }
}
