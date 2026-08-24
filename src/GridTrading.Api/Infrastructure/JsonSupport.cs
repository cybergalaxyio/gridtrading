using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridTrading.Api.Infrastructure;

public sealed class DecimalStringJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => decimal.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
        JsonTokenType.Number => reader.GetDecimal(),
        _ => throw new JsonException("Expected a decimal string or number.")
    };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("G29", CultureInfo.InvariantCulture));
}

public static class JsonSupport
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Converters = { new DecimalStringJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
        };
        return options;
    }
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}
