using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using MessagePack;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;

namespace GridTrading.Api.Exchange;

public sealed record HyperliquidSignature(string R, string S, int V);

public sealed class HyperliquidL1Signer
{
    public HyperliquidSignature SignTestnet(byte[] actionMessagePack, string privateKey, long nonce,
        string? vaultAddress = null, long? expiresAfter = null)
    {
        var actionHash = CalculateActionHash(actionMessagePack, nonce, vaultAddress, expiresAfter);
        var typedData = JsonSerializer.Serialize(new
        {
            domain = new { chainId = 1337, name = "Exchange", verifyingContract = "0x0000000000000000000000000000000000000000", version = "1" },
            types = new Dictionary<string, object>
            {
                ["Agent"] = new[] { new { name = "source", type = "string" }, new { name = "connectionId", type = "bytes32" } },
                ["EIP712Domain"] = new[]
                {
                    new { name = "name", type = "string" }, new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" }, new { name = "verifyingContract", type = "address" }
                }
            },
            primaryType = "Agent",
            message = new { source = "b", connectionId = "0x" + Convert.ToHexString(actionHash).ToLowerInvariant() }
        });
        var normalizedKey = privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? privateKey[2..] : privateKey;
        var signature = Eip712TypedDataSigner.Current.SignTypedDataV4(typedData, new EthECKey(normalizedKey));
        var hex = signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? signature[2..] : signature;
        if (hex.Length != 130) throw new CryptographicException("Unexpected EIP-712 signature length.");
        return new HyperliquidSignature("0x" + hex[..64], "0x" + hex[64..128], Convert.ToByte(hex[128..], 16));
    }

    public byte[] CalculateActionHash(byte[] actionMessagePack, long nonce, string? vaultAddress, long? expiresAfter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(actionMessagePack);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, nonce); buffer.Write(number);
        if (vaultAddress is null) buffer.Write(new byte[] { 0 });
        else
        {
            buffer.Write(new byte[] { 1 });
            var address = NormalizeAddress(vaultAddress);
            buffer.Write(Convert.FromHexString(address[2..]));
        }
        if (expiresAfter.HasValue)
        {
            buffer.Write(new byte[] { 0 });
            BinaryPrimitives.WriteInt64BigEndian(number, expiresAfter.Value); buffer.Write(number);
        }
        return Sha3Keccack.Current.CalculateHash(buffer.WrittenSpan.ToArray());
    }

    public static string DeriveAddress(string privateKey)
    {
        var normalized = privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? privateKey[2..] : privateKey;
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            throw new TradingProblemException(422, "INVALID_AGENT_PRIVATE_KEY", "Agent private key must be exactly 32 bytes of hexadecimal data.");
        return new EthECKey(normalized).GetPublicAddress().ToLowerInvariant();
    }

    public static string NormalizeAddress(string address)
    {
        var value = address.Trim().ToLowerInvariant();
        if (value.Length != 42 || !value.StartsWith("0x") || !value[2..].All(Uri.IsHexDigit))
            throw new TradingProblemException(422, "INVALID_ADDRESS", "Hyperliquid addresses must be 20-byte 0x-prefixed hexadecimal values.");
        return value;
    }

    public static byte[] PackDummyAction(long number)
    {
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(2); writer.Write("type"); writer.Write("dummy"); writer.Write("num"); writer.Write(number); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
