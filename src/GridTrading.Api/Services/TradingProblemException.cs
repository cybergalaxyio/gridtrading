namespace GridTrading.Api.Services;

public sealed class TradingProblemException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
