using System.Net;

namespace GridTrading.Api.Infrastructure;

public static class LocalAccountRequestPolicy
{
    public static bool IsAllowed(HttpContext context)
    {
        var request = context.Request;
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            !IsLoopbackHost(request.Host.Host) || !request.HasJsonContentType()) return false;
        // Credential mutations must originate from the local portal, including in development.
        if (!Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            !IsLoopbackHost(origin.Host) || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" ||
            origin.UserInfo != "") return false;
        var sameOrigin = string.Equals(origin.GetLeftPart(UriPartial.Authority),
            $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
        var developmentOrigin = origin.Scheme == "http" && origin.Port == 5173 &&
            (origin.Host == "localhost" || origin.Host == "127.0.0.1");
        return sameOrigin || developmentOrigin;
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
