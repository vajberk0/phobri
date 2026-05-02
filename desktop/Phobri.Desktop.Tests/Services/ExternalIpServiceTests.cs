using Xunit;
using Phobri.Desktop.Services;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Phobri.Desktop.Tests.Services;

// Note: ExternalIpService hits a real URL. For reliable tests,
// we'd normally mock HttpClient. This test demonstrates the approach.
public sealed class ExternalIpServiceTests
{
    [Fact]
    public void Service_CanBeConstructed()
    {
        var client = new HttpClient();
        var service = new ExternalIpService(client);
        Assert.NotNull(service);
    }

    [Fact]
    public async Task GetExternalIp_ReturnsNull_OnNetworkError()
    {
        // Use a handler that always throws
        var handler = new ThrowingHandler();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new ExternalIpService(client);

        var result = await service.GetExternalIpAsync();

        Assert.Null(result); // Should gracefully return null
    }

    [Fact]
    public async Task GetExternalIp_ReturnsNull_OnNonSuccessStatusCode()
    {
        var handler = new MockHttpMessageHandler(
            HttpStatusCode.InternalServerError, "Error");
        var client = new HttpClient(handler);
        var service = new ExternalIpService(client);

        var result = await service.GetExternalIpAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetExternalIp_ReturnsIp_FromJsonResponse()
    {
        var json = """{"ip": "203.0.113.42"}""";
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, json);
        var client = new HttpClient(handler);
        var service = new ExternalIpService(client);

        var result = await service.GetExternalIpAsync();

        Assert.Equal("203.0.113.42", result);
    }

    [Fact]
    public async Task GetExternalIp_ReturnsText_WhenPlainText()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, "198.51.100.1\n");
        var client = new HttpClient(handler);
        var service = new ExternalIpService(client);

        var result = await service.GetExternalIpAsync();

        Assert.Equal("198.51.100.1", result);
    }
}

/// <summary>
/// Simple mock HttpMessageHandler for unit testing HTTP calls.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseContent;

    public MockHttpMessageHandler(HttpStatusCode statusCode, string responseContent)
    {
        _statusCode = statusCode;
        _responseContent = responseContent;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent)
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// HttpMessageHandler that always throws for testing error paths.
/// </summary>
public sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        throw new HttpRequestException("Simulated network failure");
    }
}
