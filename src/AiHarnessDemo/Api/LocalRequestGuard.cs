namespace AiHarnessDemo.Api;

/// <summary>
/// Cross-origin browser forms can submit simple requests to localhost without CORS permission.
/// A required non-simple header forces a successful same-origin preflight before local state can
/// change.
/// </summary>
public static class LocalRequestGuard
{
    public const string HeaderName = "X-AI-Harness-Request";
    public const string HeaderValue = "1";

    public static bool IsValid(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderName, out var values) &&
        values.Count == 1 &&
        string.Equals(values[0], HeaderValue, StringComparison.Ordinal);

    public static async Task ApplyAsync(HttpContext context, RequestDelegate next)
    {
        var stateChanging =
            context.Request.Path.StartsWithSegments("/api") &&
            !IsExactDemoProxyRoute(context.Request.Path) &&
            (
                HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method) ||
                HttpMethods.IsDelete(context.Request.Method)
            );

        if (stateChanging && !IsValid(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Local request guard rejected the request.",
                status = StatusCodes.Status400BadRequest,
                detail = $"State-changing requests must include {HeaderName}: {HeaderValue}."
            });
            return;
        }

        await next(context);
    }

    internal static bool IsExactDemoProxyRoute(PathString path)
    {
        var value = path.Value;
        const string prefix = "/api/demos/";
        if (value is null ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[prefix.Length..];
        var separator = remainder.IndexOf('/');
        var instance = separator < 0
            ? remainder
            : remainder[..separator];
        return Guid.TryParseExact(instance, "D", out _);
    }
}
