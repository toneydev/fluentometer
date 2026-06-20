using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fluentometer.Tests.Capture;

/// <summary>
/// In-memory HttpMessageHandler for unit tests. No real network calls are made.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    public FakeHttpHandler(HttpStatusCode status, string body = "")
        : this(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })
    { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_respond(request));
    }
}
