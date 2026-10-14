using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Pairnets.Core.Api;
using Xunit;

namespace Pairnets.Tests.Unit;

public sealed class FriendlyErrorsTests
{
    public static TheoryData<Exception> Errors() => new()
    {
        new HttpRequestException("No connection could be made because the target machine actively refused it. (127.0.0.1:5075)"),
        new SocketException(10061),
        new TaskCanceledException("A task was canceled."),
        new TimeoutException("The operation has timed out."),
        new JsonException("'<' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0."),
        new IOException("The process cannot access the file 'C:/Users/a/x.txt' because it is being used by another process.", unchecked((int)0x80070020)),
        new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)),
        new UnauthorizedAccessException("Access to the path 'C:/Users/a/x' is denied."),
        new FileNotFoundException("Could not find file 'x'."),
        new InvalidOperationException("Sequence contains no elements"),
        new NullReferenceException("Object reference not set to an instance of an object."),
        new PairnetsNetworkException("Server error 503 Service Unavailable."),
        new PairnetsNetworkException("System.Net.Http.HttpRequestException: boom"),
        new PairnetsProtocolException("Invalid JSON from server: '<' is an invalid start"),
        new PairnetsProtocolException("Server answered 418: teapot"),
        new PairnetsProtocolException("Unexpected status 302."),
        new PairnetsAuthException("The server rejected the token (401)."),
        new PairnetsNetworkException("x", new HttpRequestException("boom")),
        new LocalFileReadException("Reading the local file failed: boom", new IOException("boom")),
        new AggregateException(new JsonException("bad json")),
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void NeverShowsPlumbing(Exception ex)
    {
        var text = FriendlyErrors.Describe(ex);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("Server error", text);
        Assert.DoesNotContain("Invalid JSON", text);
        Assert.DoesNotContain("Server answered", text);
        Assert.DoesNotContain("System.", text);
        Assert.DoesNotContain("C:/", text);
        Assert.DoesNotContain("(401)", text);
        Assert.DoesNotContain(ex.GetType().Name, text);
    }

    [Fact]
    public void SaysWhatToDoForTheKnownKinds()
    {
        Assert.Equal(FriendlyErrors.Offline, FriendlyErrors.Describe(new HttpRequestException("x")));
        Assert.Equal(FriendlyErrors.TimedOut, FriendlyErrors.Describe(new TaskCanceledException()));
        Assert.Equal(FriendlyErrors.BadAnswer, FriendlyErrors.Describe(new JsonException("x")));
        Assert.Equal(FriendlyErrors.NoAccess, FriendlyErrors.Describe(new UnauthorizedAccessException("x")));
        Assert.Equal(FriendlyErrors.DiskFull, FriendlyErrors.Describe(new IOException("x", unchecked((int)0x80070070))));
        Assert.Equal(FriendlyErrors.FileLocked, FriendlyErrors.Describe(new IOException("x", unchecked((int)0x80070020))));
        Assert.Equal(FriendlyErrors.ServerProblem, FriendlyErrors.Describe(new PairnetsNetworkException(FriendlyErrors.ServerProblem)));
        Assert.Equal(FriendlyErrors.Generic, FriendlyErrors.Describe(new InvalidOperationException("Sequence contains no elements")));
        Assert.Equal("Put the key in the keyring.", FriendlyErrors.Describe(new PlainMessageException("Put the key in the keyring.")));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task ApiClientStatusesBecomeFriendlyWords(HttpStatusCode status)
    {
        using var api = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc",
            new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("x") }));
        var ex = await Record.ExceptionAsync(() => api.GetInfoAsync(CancellationToken.None));
        Assert.NotNull(ex);
        var text = FriendlyErrors.Describe(ex);
        Assert.DoesNotContain("Server error", ex.Message);
        Assert.DoesNotContain("Server error", text);
        Assert.DoesNotContain("Server answered", ex.Message);
        Assert.DoesNotContain(((int)status).ToString(), text);
        Assert.DoesNotContain("Exception", text);
    }

    [Fact]
    public async Task GarbageAnswerIsNotInvalidJsonText()
    {
        using var api = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc",
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", System.Text.Encoding.UTF8, "application/json") }));
        var ex = await Assert.ThrowsAsync<PairnetsProtocolException>(() => api.GetInfoAsync(CancellationToken.None));
        Assert.Equal(FriendlyErrors.BadAnswer, ex.Message);
        Assert.Contains("Invalid JSON", ex.Details);
    }

    [Fact]
    public async Task DroppedConnectionIsOfflineWords()
    {
        using var api = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc",
            new StubHandler(_ => throw new HttpRequestException("No such host is known.", new SocketException(11001))));
        var ex = await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetInfoAsync(CancellationToken.None));
        Assert.Equal(FriendlyErrors.Offline, ex.Message);
        Assert.Contains("No such host", FriendlyErrors.Technical(ex));
    }

    [Fact]
    public void TechnicalKeepsEveryDetailForTheLog()
    {
        var ex = new PairnetsNetworkException(FriendlyErrors.ServerProblem, new HttpRequestException("inner"), details: "Server error 502 Bad Gateway");
        var text = FriendlyErrors.Technical(ex);
        Assert.Contains("PairnetsNetworkException", text);
        Assert.Contains("Server error 502 Bad Gateway", text);
        Assert.Contains("HttpRequestException: inner", text);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
