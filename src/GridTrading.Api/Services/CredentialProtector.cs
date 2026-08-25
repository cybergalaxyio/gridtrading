using System.Security.Cryptography;
using System.Text;

namespace GridTrading.Api.Services;

public sealed class CredentialProtector
{
    private readonly byte[]? _key;

    public CredentialProtector(IConfiguration configuration)
    {
        var encoded = configuration["GRID_TRADING_CREDENTIAL_KEY"] ?? Environment.GetEnvironmentVariable("GRID_TRADING_CREDENTIAL_KEY");
        if (string.IsNullOrWhiteSpace(encoded)) return;
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length == 32) _key = key;
        }
        catch (FormatException) { }
    }

    public bool IsConfigured => _key is { Length: 32 };

    public string Protect(string plaintext)
    {
        EnsureConfigured();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var input = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        using var aes = new AesGcm(_key!, 16);
        aes.Encrypt(nonce, input, ciphertext, tag, "grid-trading:hyperliquid:testnet:v1"u8.ToArray());
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public string Unprotect(string protectedValue)
    {
        EnsureConfigured();
        var payload = Convert.FromBase64String(protectedValue);
        if (payload.Length < 29) throw new CryptographicException("Credential payload is invalid.");
        var nonce = payload[..12]; var tag = payload[12..28]; var ciphertext = payload[28..];
        var output = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key!, 16);
        aes.Decrypt(nonce, ciphertext, tag, output, "grid-trading:hyperliquid:testnet:v1"u8.ToArray());
        return Encoding.UTF8.GetString(output);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new TradingProblemException(503, "CREDENTIAL_KEY_NOT_CONFIGURED",
                "Set GRID_TRADING_CREDENTIAL_KEY to a base64-encoded 32-byte key before storing an API Wallet.");
    }
}
