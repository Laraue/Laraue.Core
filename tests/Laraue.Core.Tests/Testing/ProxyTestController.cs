using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Laraue.Core.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Laraue.Core.Tests.Testing;

[ApiController]
[Route("api/proxy-test")]
public class ProxyTestController : ControllerBase
{
    [HttpPost("echo")]
    public Task<EchoRequest> Echo([FromBody] EchoRequest request)
    {
        return Task.FromResult(request);
    }

    [HttpPost("snake-case")]
    public Task<SnakeCaseEcho> EchoSnakeCase([FromBody] SnakeCaseRequest request)
    {
        return Task.FromResult(new SnakeCaseEcho(request.FirstName, request.AuthDate));
    }

    [HttpGet("items/{id}")]
    public Task<ItemResponse> GetItem(long id, [FromQuery] string? filter)
    {
        return Task.FromResult(new ItemResponse(id, filter));
    }

    [HttpGet("unauthorized")]
    public Task<IActionResult> ReturnUnauthorized()
    {
        // Not Unauthorized(): [ApiController] would add a ProblemDetails body, while a real 401 from
        // the authentication middleware has none.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.FromResult<IActionResult>(new EmptyResult());
    }

    [HttpGet("not-found")]
    public Task<IActionResult> ReturnNotFound()
    {
        return Task.FromResult<IActionResult>(NotFound(new ErrorResponse("Item is missing", null)));
    }

    [HttpGet("plain-text-error")]
    public Task<IActionResult> ReturnPlainTextError()
    {
        return Task.FromResult<IActionResult>(StatusCode(502, "<html>Bad gateway</html>"));
    }
}

public sealed class EchoRequest
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public List<string>? Tags { get; init; }
}

public sealed class SnakeCaseRequest
{
    [JsonPropertyName("first_name")]
    public string FirstName { get; init; } = string.Empty;

    [JsonPropertyName("auth_date")]
    public long AuthDate { get; init; }
}

public sealed record SnakeCaseEcho(string FirstName, long AuthDate);

public sealed record ItemResponse(long Id, string? Filter);
