using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ouroboros.Modules.Identity.Application.Security;

// Refresh token opaco: 32 bytes aleatórios em Base64Url. O banco guarda só o SHA-256 do valor; um
// hash rápido basta porque o token tem 256 bits de entropia, ao contrário de uma senha.
internal static class RefreshTokens
{
    public const int SizeInBytes = 32;

    public static (string Token, byte[] Hash) Generate()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SizeInBytes));
        return (token, Hash(token));
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
