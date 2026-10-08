using System.Security.Cryptography;
using System.Text;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Platform;

/// <summary>Encrypts the token with Windows DPAPI for the current user only.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Pairnets.Core.PairnetsInfo.DpapiEntropy);
    private static readonly byte[] TetherEntropy = Encoding.UTF8.GetBytes(Pairnets.Core.Legacy.TetherNames.DpapiEntropy);

    public string Protect(string plainText) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedText)
    {
        var data = Convert.FromBase64String(protectedText);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Saved by Tether: readable the same way with its entropy (re-saved as Pairnets' on the next settings save).
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, TetherEntropy, DataProtectionScope.CurrentUser));
        }
    }
}
