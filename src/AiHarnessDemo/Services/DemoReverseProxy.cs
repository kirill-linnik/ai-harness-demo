using System.Net;
using System.Net.Http.Headers;

namespace AiHarnessDemo.Services;

/// <summary>
/// Narrow reverse proxy for one verified, healthy, owned loopback demo. The runtime manager is the
/// only target resolver; request data can never select a host or port.
/// </summary>
public sealed class DemoReverseProxy : IDisposable
{
    internal const string IsolationPolicy =
        "sandbox allow-scripts allow-forms; default-src 'self' data: blob:; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; " +
        "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; media-src 'self' data: blob:; " +
        "connect-src 'self'; form-action 'self'; object-src 'none'; " +
        "base-uri 'none'; frame-ancestors 'self'";

    private static readonly HashSet<string> RequestHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept",
            "Accept-Encoding",
            "Accept-Language",
            "Cache-Control",
            "Content-Type",
            "If-Match",
            "If-Modified-Since",
            "If-None-Match",
            "If-Unmodified-Since",
            "Range",
            "User-Agent"
        };

    private static readonly HashSet<string> ResponseHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept-Ranges",
            "Cache-Control",
            "Content-Disposition",
            "Content-Encoding",
            "Content-Language",
            "Content-Length",
            "Content-Range",
            "Content-Type",
            "ETag",
            "Expires",
            "Last-Modified",
            "Location",
            "Vary"
        };

    private readonly DemoRuntimeManager _runtime;
    private readonly HttpClient _client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public DemoReverseProxy(DemoRuntimeManager runtime)
    {
        _runtime = runtime;
    }

    public async Task ProxyAsync(
        Guid instanceId,
        string? path,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ApplyIsolationHeaders(context.Response);
        context.Response.OnStarting(
            static state =>
            {
                ApplyIsolationHeaders((HttpResponse)state);
                return Task.CompletedTask;
            },
            context.Response);

        var (record, _) = await _runtime.GetProxyTargetAsync(
            instanceId,
            cancellationToken);
        if (HttpMethods.IsOptions(context.Request.Method) &&
            IsOpaqueOriginRequest(context.Request))
        {
            WriteOpaqueCorsPreflight(context);
            return;
        }

        var escapedPath = EscapePathPreservingTrailingSlash(path);
        var target = new UriBuilder(
            Uri.UriSchemeHttp,
            "127.0.0.1",
            record.AssignedPort!.Value,
            "/" + escapedPath)
        {
            Query = context.Request.QueryString.HasValue
                ? context.Request.QueryString.Value![1..]
                : string.Empty
        }.Uri;
        using var upstream = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            target);
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
        upstream.Headers.TryAddWithoutValidation(
            "X-Forwarded-Prefix",
            $"/api/demos/{instanceId:D}");
        if (IsOpaqueOriginRequest(context.Request))
        {
            upstream.Headers.TryAddWithoutValidation(
                "Origin",
                $"http://127.0.0.1:{record.AssignedPort.Value}");
        }
        if (context.Request.ContentLength is > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstream.Content = new StreamContent(context.Request.Body);
        }
        foreach (var header in context.Request.Headers)
        {
            if (!RequestHeaders.Contains(header.Key))
            {
                continue;
            }
            if (!upstream.Headers.TryAddWithoutValidation(
                    header.Key,
                    header.Value.ToArray()))
            {
                upstream.Content?.Headers.TryAddWithoutValidation(
                    header.Key,
                    header.Value.ToArray());
            }
        }

        using var response = await _client.SendAsync(
            upstream,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!ResponseHeaders.Contains(header.Key))
            {
                continue;
            }
            if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase) &&
                response.Headers.Location is { } location)
            {
                context.Response.Headers.Location = RewriteLocation(
                    location,
                    target,
                    instanceId,
                    record.AssignedPort.Value);
                continue;
            }
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        context.Response.Headers.Remove("transfer-encoding");
        ApplyOpaqueCorsResponse(context);
        await response.Content.CopyToAsync(
            context.Response.Body,
            cancellationToken);
    }

    private static string EscapePathPreservingTrailingSlash(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }
        var trailingSlash = path.EndsWith("/", StringComparison.Ordinal);
        var escaped = string.Join(
            "/",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return trailingSlash && escaped.Length > 0
            ? escaped + "/"
            : escaped;
    }

    private static string RewriteLocation(
        Uri location,
        Uri upstreamRequestUri,
        Guid instanceId,
        int upstreamPort)
    {
        var resolved = location.IsAbsoluteUri
            ? location
            : new Uri(upstreamRequestUri, location);
        var loopback =
            string.Equals(resolved.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(resolved.Host, out var address) &&
            IPAddress.IsLoopback(address);
        if (!loopback ||
            resolved.Port != upstreamPort)
        {
            throw new DemoRuntimeException(
                DemoConflictCodes.InvalidTransition,
                "The live demo attempted to redirect outside its owned loopback endpoint.",
                StatusCodes.Status502BadGateway);
        }
        var proxyPrefix = $"/api/demos/{instanceId:D}";
        var pathAndSuffix = resolved.PathAndQuery + resolved.Fragment;
        return resolved.AbsolutePath.Equals(proxyPrefix, StringComparison.Ordinal) ||
               resolved.AbsolutePath.StartsWith(
                   proxyPrefix + "/",
                   StringComparison.Ordinal)
            ? pathAndSuffix
            : proxyPrefix + pathAndSuffix;
    }

    private static bool IsOpaqueOriginRequest(HttpRequest request) =>
        request.Headers.TryGetValue("Origin", out var origin) &&
        origin.Count == 1 &&
        string.Equals(origin[0], "null", StringComparison.Ordinal);

    private static void WriteOpaqueCorsPreflight(HttpContext context)
    {
        var requestedMethod =
            context.Request.Headers["Access-Control-Request-Method"].ToString();
        if (!new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" }
                .Contains(requestedMethod, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers["Access-Control-Allow-Origin"] = "null";
        context.Response.Headers["Access-Control-Allow-Methods"] = requestedMethod;
        var requestedHeaders =
            context.Request.Headers["Access-Control-Request-Headers"].ToString();
        if (!string.IsNullOrWhiteSpace(requestedHeaders))
        {
            context.Response.Headers["Access-Control-Allow-Headers"] =
                requestedHeaders;
        }
        context.Response.Headers.Append("Vary", "Origin");
        context.Response.Headers.Append(
            "Vary",
            "Access-Control-Request-Method");
        context.Response.Headers.Append(
            "Vary",
            "Access-Control-Request-Headers");
    }

    private static void ApplyOpaqueCorsResponse(HttpContext context)
    {
        if (!IsOpaqueOriginRequest(context.Request))
        {
            return;
        }
        context.Response.Headers["Access-Control-Allow-Origin"] = "null";
        context.Response.Headers.Append("Vary", "Origin");
    }

    internal static void ApplyIsolationHeaders(HttpResponse response)
    {
        response.Headers["Content-Security-Policy"] = IsolationPolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    }

    public void Dispose() => _client.Dispose();
}
