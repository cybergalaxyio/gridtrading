using System.Text.Json;
using GridTrading.Api.Infrastructure;

namespace GridTrading.Api.Tests;

public sealed class DecimalStringJsonConverterTests
{
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"invalid\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"1e100\"")]
    [InlineData("1e100")]
    public void InvalidDecimalIsAJsonErrorInsteadOfAServerError(string value)
    {
        var error = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<NumberInput>("{\"amount\":" + value + "}", JsonSupport.Options));
        Assert.Equal("$.amount", error.Path);
    }

    [Theory]
    [InlineData("\"0\"", "0")]
    [InlineData("\"0.00000001\"", "0.00000001")]
    [InlineData("\"-12.50\"", "-12.5")]
    [InlineData("\"1e-3\"", "0.001")]
    [InlineData("0.5", "0.5")]
    public void ValidDecimalsPreservePrecision(string value, string expected)
    {
        var parsed = JsonSerializer.Deserialize<NumberInput>("{\"amount\":" + value + "}", JsonSupport.Options)!;
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), parsed.Amount);
        Assert.Equal(parsed, JsonSerializer.Deserialize<NumberInput>(JsonSerializer.Serialize(parsed, JsonSupport.Options), JsonSupport.Options));
    }

    private sealed record NumberInput(decimal Amount);
}
