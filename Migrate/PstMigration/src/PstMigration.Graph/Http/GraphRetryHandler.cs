using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace PstMigration.Graph.Http;

/// <summary>
/// Retries transient Graph failures (429 + 5xx) honoring Retry-After, with
/// exponential backoff + jitter. Spec section 14. The request is cloned per
/// attempt because an <see cref="HttpRequestMessage"/> can only be sent once.
/// </summary>
public sealed class GraphRetryHandler : DelegatingHandler
{
    private static readonly HttpStatusCode[] Transient =
    {
        HttpStatusCode.TooManyRequests,          // 429
        HttpStatusCode.InternalServerError,      // 500
        HttpStatusCode.BadGateway,               // 502
        HttpStatusCode.ServiceUnavailable,       // 503
        HttpStatusCode.GatewayTimeout,           // 504
    };

    private readonly ILogger<GraphRetryHandler> _logger;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _maxDelay = TimeSpan.FromMinutes(5);

    public GraphRetryHandler(ILogger<GraphRetryHandler> logger, int maxRetries = 10)
    {
        _logger = logger;
        _maxRetries = maxRetries;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the content once so the request can be replayed on retry.
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = request.Content?.Headers.ContentType;

        var attempt = 0;
        while (true)
        {
            using var attemptRequest = Clone(request, body, contentType);
            var response = await base.SendAsync(attemptRequest, cancellationToken);

            if (!Array.Exists(Transient, s => s == response.StatusCode) || attempt >= _maxRetries)
                return response;

            var delay = ComputeDelay(response, attempt);
            _logger.LogWarning("Graph {Status} on {Path}; retry {Attempt}/{Max} in {Delay}s",
                (int)response.StatusCode, request.RequestUri?.AbsolutePath, attempt + 1, _maxRetries, delay.TotalSeconds);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
            attempt++;
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage src, byte[]? body, MediaTypeHeaderValue? contentType)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri);
        foreach (var h in src.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (contentType is not null) clone.Content.Headers.ContentType = contentType;
        }
        return clone;
    }

    private TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Delta is { } d) return Cap(d);
            if (retryAfter.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero) return Cap(until);
            }
        }

        var backoff = TimeSpan.FromSeconds(_initialDelay.TotalSeconds * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        return Cap(backoff + jitter);
    }

    private TimeSpan Cap(TimeSpan t) => t > _maxDelay ? _maxDelay : t;
}
