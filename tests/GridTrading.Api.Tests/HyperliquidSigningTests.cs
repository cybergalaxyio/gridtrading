using GridTrading.Api.Exchange;
using GridTrading.Api.Services;
using MessagePack;
using System.Text.Json;

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

    [Theory]
    [InlineData("103.375", 2, "0.01")]
    [InlineData("1234.5", 2, "0.1")]
    [InlineData("0.001234", 0, "0.000001")]
    public void TickSizeFollowsPriceMagnitudeAndSizeDecimals(string referencePrice, int sizeDecimals, string expected) =>
        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            HyperliquidWireCodec.TickSize(
                decimal.Parse(referencePrice, System.Globalization.CultureInfo.InvariantCulture),
                sizeDecimals));

    [Fact]
    public void StrategyOrderUsesPlainLimitWithoutReduceOnly()
    {
        var order = new HyperliquidLimitOrder(5, true, "99.1", "0.2", false, "Alo",
            "0x11111111111111111111111111111111");

        var reader = new MessagePackReader(HyperliquidWireCodec.PackOrderAction([order]));
        Assert.Equal(3, reader.ReadMapHeader());
        Assert.Equal("type", reader.ReadString());
        Assert.Equal("order", reader.ReadString());
        Assert.Equal("orders", reader.ReadString());
        Assert.Equal(1, reader.ReadArrayHeader());
        Assert.Equal(7, reader.ReadMapHeader());
        Assert.Equal("a", reader.ReadString()); Assert.Equal(5, reader.ReadInt32());
        Assert.Equal("b", reader.ReadString()); Assert.True(reader.ReadBoolean());
        Assert.Equal("p", reader.ReadString()); Assert.Equal("99.1", reader.ReadString());
        Assert.Equal("s", reader.ReadString()); Assert.Equal("0.2", reader.ReadString());
        Assert.Equal("r", reader.ReadString()); Assert.False(reader.ReadBoolean());
        Assert.Equal("t", reader.ReadString()); Assert.Equal(1, reader.ReadMapHeader());
        Assert.Equal("limit", reader.ReadString()); Assert.Equal(1, reader.ReadMapHeader());
        Assert.Equal("tif", reader.ReadString()); Assert.Equal("Alo", reader.ReadString());
        Assert.Equal("c", reader.ReadString()); Assert.Equal(order.Cloid, reader.ReadString());
        Assert.Equal("grouping", reader.ReadString());
        Assert.Equal("na", reader.ReadString());
        Assert.True(reader.End);
    }

    [Fact]
    public void ModifyOrderUsesCloidAndCompleteLimitOrder()
    {
        const string cloid = "0x11111111111111111111111111111111";
        var order = new HyperliquidLimitOrder(5, false, "100.19", "0.2", false, "Alo", cloid);

        var reader = new MessagePackReader(HyperliquidWireCodec.PackModifyAction(cloid, order));
        Assert.Equal(2, reader.ReadMapHeader());
        Assert.Equal("type", reader.ReadString()); Assert.Equal("batchModify", reader.ReadString());
        Assert.Equal("modifies", reader.ReadString()); Assert.Equal(1, reader.ReadArrayHeader());
        Assert.Equal(2, reader.ReadMapHeader());
        Assert.Equal("oid", reader.ReadString()); Assert.Equal(cloid, reader.ReadString());
        Assert.Equal("order", reader.ReadString());
        Assert.Equal(7, reader.ReadMapHeader());
        Assert.Equal("a", reader.ReadString()); Assert.Equal(5, reader.ReadInt32());
        Assert.Equal("b", reader.ReadString()); Assert.False(reader.ReadBoolean());
        Assert.Equal("p", reader.ReadString()); Assert.Equal("100.19", reader.ReadString());
        Assert.Equal("s", reader.ReadString()); Assert.Equal("0.2", reader.ReadString());
        Assert.Equal("r", reader.ReadString()); Assert.False(reader.ReadBoolean());
        Assert.Equal("t", reader.ReadString()); Assert.Equal(1, reader.ReadMapHeader());
        Assert.Equal("limit", reader.ReadString()); Assert.Equal(1, reader.ReadMapHeader());
        Assert.Equal("tif", reader.ReadString()); Assert.Equal("Alo", reader.ReadString());
        Assert.Equal("c", reader.ReadString()); Assert.Equal(cloid, reader.ReadString());
        Assert.True(reader.End);
    }

    [Fact]
    public void HyperliquidMinimumOrderNotionalMatchesVenue() =>
        Assert.Equal(10m, HyperliquidInfoClient.MinimumOrderNotional);

    [Theory]
    [InlineData("waitingForFill")]
    [InlineData("waitingForTrigger")]
    public void StringWaitingStatusDoesNotCrashOrderParser(string exchangeStatus)
    {
        using var json = JsonDocument.Parse($"\"{exchangeStatus}\"");

        var result = HyperliquidTradingClient.ParseOrderStatus(json.RootElement, "0xchild");

        Assert.Equal("WAITING", result.Status);
        Assert.Null(result.Error);
        Assert.Equal("0xchild", result.Cloid);
    }
}
