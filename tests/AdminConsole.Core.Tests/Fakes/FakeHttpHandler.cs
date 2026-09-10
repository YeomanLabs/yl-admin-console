using System.Net;
using System.Text;

namespace AdminConsole.Core.Tests.Fakes;

/// <summary>Scripted <see cref="HttpMessageHandler"/>: responds in order from a queue and records every request.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public FakeHttpHandler Enqueue(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null) =>
        Enqueue(_ => Json(status, json, configure));

    public static HttpResponseMessage Json(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        configure?.Invoke(response);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
        }

        return _responders.Dequeue()(request);
    }

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);
}
