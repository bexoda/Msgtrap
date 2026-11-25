using System.Net;
using Xunit;

namespace Msgtrap.Tests;

/// <summary>A web page the developer visits must not be able to read or change the inbox.</summary>
public sealed class BrowserGuardTests(MsgtrapFactory factory) : IClassFixture<MsgtrapFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> GetAsync(string path, string? origin = null, string? host = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task ControlPlane_RejectsCrossOrigin()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/control/messages", "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync("/api/control/messages", "null")).StatusCode);
    }

    [Theory]
    [InlineData("/API/control/messages")]
    [InlineData("/api/CONTROL/messages")]
    [InlineData("/Api/Control/settings")]
    public async Task ControlPlane_CaseVariants_AreStillGuarded(string path)
    {
        var response = await GetAsync(path, "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ControlPlane_AllowsSameOrigin_WithoutCorsHeaders()
    {
        var response = await GetAsync("/api/control/messages", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ProviderApi_StillAllowsCrossOriginPreflight()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v2/sms/send");
        request.Headers.Add("Origin", "http://localhost:3000");
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected_AgainstDnsRebinding()
    {
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await GetAsync("/api/control/messages", host: "evil.example")).StatusCode);
    }

    [Fact]
    public async Task Responses_CarryHardeningHeaders()
    {
        var response = await GetAsync("/");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
