using GridTrading.Api.Exchange;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidSigningTests
{
    private const string PrivateKey = "0x0123456789012345678901234567890123456789012345678901234567890123";

    [Fact]
    public void TestnetL1SignatureMatchesOfficialPythonSdkVector()
    {
        var action = HyperliquidL1Signer.PackDummyAction(100_000_000_000L);
        var signature = new HyperliquidL1Signer().SignTestnet(action, PrivateKey, 0);
        Assert.Equal("0x542af61ef1f429707e3c76c5293c80d01f74ef853e34b76efffcb57e574f9510", signature.R);
        Assert.Equal("0x17b8b32f086e8cdede991f1e2c529f5dd5297cbe8128500e00cbaf766204a613", signature.S);
        Assert.Equal(28, signature.V);
    }

    [Fact]
    public void DerivesExpectedAgentAddress() =>
        Assert.Equal("0x14791697260e4c9a71f18484c9f997b308e59325", HyperliquidL1Signer.DeriveAddress(PrivateKey));

    [Fact]
    public void CloidIsStable128BitHex()
    {
        var first = HyperliquidWireCodec.CreateCloid("strategy/cycle/buy/0/1");
        Assert.Equal(first, HyperliquidWireCodec.CreateCloid("strategy/cycle/buy/0/1"));
        Assert.Matches("^0x[0-9a-f]{32}$", first);
    }

    [Theory]
    [InlineData("145.256", 2, "145.26")]
    [InlineData("0.0123456", 2, "0.0123")]
    public void PriceUsesFiveSignificantFigures(string value, int sizeDecimals, string expected) =>
        Assert.Equal(expected, HyperliquidWireCodec.PriceToWire(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), sizeDecimals));
}
