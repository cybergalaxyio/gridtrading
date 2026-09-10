using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace GridTrading.Api.Exchange;

public sealed record HyperliquidLimitOrder(int Asset, bool IsBuy, string Price, string Size, bool ReduceOnly, string Tif, string Cloid);

public static class HyperliquidWireCodec
{
    public static byte[] PackOrderAction(IReadOnlyList<HyperliquidLimitOrder> orders)
    {
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(3); writer.Write("type"); writer.Write("order"); writer.Write("orders");
        writer.WriteArrayHeader(orders.Count);
        foreach (var order in orders) WriteLimitOrder(ref writer, order);
        writer.Write("grouping"); writer.Write("na"); writer.Flush(); return buffer.WrittenSpan.ToArray();
    }

    public static byte[] PackModifyAction(string oid, HyperliquidLimitOrder order)
    {
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(2);
        writer.Write("type"); writer.Write("batchModify");
        writer.Write("modifies"); writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2);
        writer.Write("oid"); writer.Write(oid);
        writer.Write("order"); WriteLimitOrder(ref writer, order);
        writer.Flush(); return buffer.WrittenSpan.ToArray();
    }

    private static void WriteLimitOrder(ref MessagePackWriter writer, HyperliquidLimitOrder order)
    {
        writer.WriteMapHeader(7);
        writer.Write("a"); writer.Write(order.Asset);
        writer.Write("b"); writer.Write(order.IsBuy);
        writer.Write("p"); writer.Write(order.Price);
        writer.Write("s"); writer.Write(order.Size);
        writer.Write("r"); writer.Write(order.ReduceOnly);
        writer.Write("t"); writer.WriteMapHeader(1); writer.Write("limit"); writer.WriteMapHeader(1); writer.Write("tif"); writer.Write(order.Tif);
        writer.Write("c"); writer.Write(order.Cloid);
    }

    public static byte[] PackCancelByCloidAction(int asset, string cloid)
    {
        var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(2); writer.Write("type"); writer.Write("cancelByCloid"); writer.Write("cancels"); writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2); writer.Write("asset"); writer.Write(asset); writer.Write("cloid"); writer.Write(cloid);
        writer.Flush(); return buffer.WrittenSpan.ToArray();
    }

    public static string CreateCloid(string stableId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableId));
        return "0x" + Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    public static string PriceToWire(decimal price, int sizeDecimals)
    {
        if (price <= 0m) throw new TradingProblemException(422, "INVALID_PRICE", "Order price must be positive.");
        var decimals = PriceDecimals(price, sizeDecimals);
        return Math.Round(price, decimals, MidpointRounding.ToEven).ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static decimal TickSize(decimal referencePrice, int sizeDecimals)
    {
        if (referencePrice <= 0m) throw new TradingProblemException(422, "INVALID_PRICE", "Reference price must be positive.");
        var tick = 1m;
        for (var i = 0; i < PriceDecimals(referencePrice, sizeDecimals); i++) tick /= 10m;
        return tick;
    }

    private static int PriceDecimals(decimal price, int sizeDecimals)
    {
        var magnitude = (int)Math.Floor(Math.Log10((double)price));
        var significantDecimals = Math.Max(0, 4 - magnitude);
        var maximumDecimals = Math.Max(0, 6 - sizeDecimals);
        return Math.Min(significantDecimals, maximumDecimals);
    }

    public static string SizeToWire(decimal size, int sizeDecimals)
    {
        if (size <= 0m) throw new TradingProblemException(422, "INVALID_SIZE", "Order size must be positive.");
        var rounded = Math.Round(size, sizeDecimals, MidpointRounding.ToZero);
        if (rounded <= 0m) throw new TradingProblemException(422, "SIZE_BELOW_PRECISION", "Order size is below Hyperliquid precision.");
        return rounded.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
    }
}
