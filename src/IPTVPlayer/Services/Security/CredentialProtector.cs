using System.Security.Cryptography;
using System.Text;

namespace IPTVPlayer.Services.Security;

/// <summary>
/// Encripta credenciais com DPAPI (âmbito do utilizador atual do Windows):
/// só a mesma conta, no mesmo PC, consegue desencriptar.
/// </summary>
public sealed class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IPTVPlayer/credentials/v1");

    public byte[] Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);
        try
        {
            return ProtectBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(byte[] protectedData)
    {
        var bytes = UnprotectBytes(protectedData);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public byte[] ProtectBytes(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] UnprotectBytes(byte[] protectedData)
    {
        try
        {
            return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new UserFacingException(
                "Não foi possível desencriptar os dados guardados (foram criados noutra conta do Windows?). " +
                "Remove a playlist e adiciona-a de novo.", ex);
        }
    }
}
