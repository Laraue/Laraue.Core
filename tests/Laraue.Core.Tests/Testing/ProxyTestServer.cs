using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Laraue.Core.Tests.Testing;

/// <summary>
/// An in-memory ASP.NET Core server hosting <see cref="ProxyTestController"/>, shared by the tests of
/// one class.
/// </summary>
public sealed class ProxyTestServer : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient CreateClient() => _app!.GetTestClient();

    public Laraue.Core.Testing.Http.Proxy<ProxyTestController> Proxy() =>
        new(CreateClient(), _app!.Services);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(ProxyTestController).Assembly);

        _app = builder.Build();
        _app.MapControllers();

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
