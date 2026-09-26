using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Laraue.Core.Exceptions.Web;
using Xunit;

namespace Laraue.Core.Tests.Testing;

public class ProxyTests(ProxyTestServer server) : IClassFixture<ProxyTestServer>
{
    [Fact]
    public async Task Execute_ShouldSendBodyArgument_WhenItIsCreatedInline()
    {
        var response = await server.Proxy().Execute(x => x.Echo(new EchoRequest { Name = "inline", Count = 2 }));

        Assert.Equal("inline", response!.Name);
        Assert.Equal(2, response.Count);
    }

    [Fact]
    public async Task Execute_ShouldSendBodyArgument_WhenItIsVariable()
    {
        var request = new EchoRequest { Name = "variable", Count = 3, Tags = ["a", "b"] };

        var response = await server.Proxy().Execute(x => x.Echo(request));

        Assert.Equal("variable", response!.Name);
        Assert.Equal(["a", "b"], response.Tags);
    }

    [Fact]
    public async Task Execute_ShouldSendBodyArgumentUnwrapped_WhenItComesFromMethodCall()
    {
        var response = await server.Proxy().Execute(x => x.Echo(CreateRequest("from method", 4)));

        Assert.Equal("from method", response!.Name);
        Assert.Equal(4, response.Count);
    }

    [Fact]
    public async Task Execute_ShouldUseJsonPropertyNames_WhenBodyTypeDefinesThem()
    {
        var response = await server.Proxy().Execute(x => x.EchoSnakeCase(
            new SnakeCaseRequest { FirstName = "Ada", AuthDate = 1790390139 }));

        Assert.Equal("Ada", response!.FirstName);
        Assert.Equal(1790390139, response.AuthDate);
    }

    [Fact]
    public async Task Execute_ShouldPutRouteAndQueryArgumentsIntoUrl()
    {
        var response = await server.Proxy().Execute(x => x.GetItem(42, "open"));

        Assert.Equal(new ItemResponse(42, "open"), response);
    }

    [Fact]
    public async Task Execute_ShouldThrowWithStatusCode_WhenErrorResponseHasNoBody()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => server.Proxy().Execute(x => x.ReturnUnauthorized()));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task Execute_ShouldThrowWithTypedInnerException_WhenServerReturnsErrorResponse()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => server.Proxy().Execute(x => x.ReturnNotFound()));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        var inner = Assert.IsType<NotFoundException>(exception.InnerException);
        Assert.Equal("Item is missing", inner.Message);
    }

    [Fact]
    public async Task Execute_ShouldThrowWithStatusCode_WhenErrorBodyIsNotJson()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => server.Proxy().Execute(x => x.ReturnPlainTextError()));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("Undeserializable response", exception.Message);
    }

    private static EchoRequest CreateRequest(string name, int count) => new() { Name = name, Count = count };
}
