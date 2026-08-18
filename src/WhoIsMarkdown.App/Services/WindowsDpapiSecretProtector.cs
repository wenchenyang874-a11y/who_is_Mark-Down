using System.Security.Cryptography;
using System.Text;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Protects the ImgBB API key with Windows DPAPI for the current user. A copied
/// settings file therefore cannot reveal or reuse the credential under another
/// Windows account, and no application-managed encryption key needs to be stored.
/// </summary>
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes(
        "WIMD/ImageHosting/ImgBB/v1");

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        byte[] clearBytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(
                clearBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (CryptographicException exception)
        {
            throw new SecretProtectionException("无法使用当前 Windows 账户保护 ImgBB API Key。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new SecretProtectionException("已保存的 ImgBB API Key 格式损坏，请重新设置。", exception);
        }

        try
        {
            byte[] clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clearBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        catch (CryptographicException exception)
        {
            throw new SecretProtectionException(
                "当前 Windows 账户无法解密 ImgBB API Key，请在图片设置中重新填写。",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
