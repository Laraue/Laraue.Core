using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Laraue.Core.Exceptions;
using Laraue.Core.Exceptions.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using FromQueryAttribute = Microsoft.AspNetCore.Mvc.FromQueryAttribute;

namespace Laraue.Core.Testing.Http;

/// <summary>
/// Calls a controller action over a real <see cref="HttpClient"/> by expressing the call as a
/// strongly-typed expression against the controller, e.g.
/// <c>proxy.Execute(c =&gt; c.GetIssue(issueId))</c>. Resolves the route, HTTP method and
/// parameter binding (path/query/body/form/file) from the same attributes ASP.NET Core uses to
/// bind the action, so tests don't have to hand-build URLs or request payloads.
/// </summary>
public class Proxy<TController>(HttpClient client, IServiceProvider services) where TController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly Regex TemplateParameterRegex = new("{(\\w+)(?::(\\w+))?}", RegexOptions.Compiled);

    /// <summary>
    /// The DI container the proxy was created with. Exposed so consumers can add their own
    /// extension methods (e.g. minting an auth token via an app-specific service) without the
    /// proxy itself needing to know about that service.
    /// </summary>
    public IServiceProvider Services => services;

    public async Task<T?> Execute<T>(Expression<Func<TController, Task<T>>> makeCall)
    {
        var nonGenericCall = ConvertToNonGeneric(makeCall);
        var response = await ExecuteInternal(nonGenericCall);
        if (typeof(T) == typeof(string))
            return (dynamic) await response.Content.ReadAsStringAsync();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    public Task Execute(Expression<Func<TController, Task>> makeCall)
    {
        return ExecuteInternal(makeCall);
    }

    public Proxy<TController> WithAuthorizationToken(string token)
    {
        const string headerName = "Authorization";
        client.DefaultRequestHeaders.Remove(headerName);
        client.DefaultRequestHeaders.Add(headerName, $"Bearer {token}");
        return this;
    }

    private async Task<HttpResponseMessage> ExecuteInternal(Expression<Func<TController, Task>> makeCall)
    {
        var controllerPath = GetControllerRoute();
        var methodExpr = GetMethodCallExpression(makeCall);
        var httpAttribute = GetHttpMethodAttribute(methodExpr.Method);
        var templateParameters = GetTemplateParameters(httpAttribute.Template);

        var boundArguments = BindArguments(methodExpr, templateParameters);

        var fullPath = BuildPath(controllerPath, httpAttribute, templateParameters, boundArguments);
        var (content, bodyDescription) = BuildRequestContent(boundArguments);

        var response = await SendRequest(httpAttribute, fullPath, content);
        await HandleNonSuccessCode(response, bodyDescription);
        return response;
    }

    private string GetControllerRoute()
    {
        var controller = typeof(TController);
        var routeAttribute = controller.GetCustomAttribute<RouteAttribute>()
            ?? throw new InvalidOperationException($"Route attribute on {controller} excepted");
        return routeAttribute.Template;
    }

    private static MethodCallExpression GetMethodCallExpression(Expression<Func<TController, Task>> makeCall)
    {
        var call = makeCall.Body;
        if (call is UnaryExpression unary)
            call = unary.Operand;

        if (call is not MethodCallExpression methodExpr)
            throw new InvalidOperationException($"Method call {call} excepted");

        return methodExpr;
    }

    private static HttpMethodAttribute GetHttpMethodAttribute(MethodInfo method)
    {
        return method.GetCustomAttribute<HttpMethodAttribute>(true)
            ?? throw new InvalidOperationException($"Method {method} should be marked as HTTP attribute, e.g. [HttpGet] to be called.");
    }

    /// <summary>
    /// Resolves the bind source (path/query/body/form) that should be used for the given controller action parameter.
    /// </summary>
    private static BindType? GetBindType(ParameterInfo parameter, TemplateParameter[] templateParameters)
    {
        if (templateParameters.Select(x => x.Name).Contains(parameter.Name))
            return BindType.FromPath;

        if (parameter.GetCustomAttribute<FromFormAttribute>() != null)
            return BindType.FromForm;

        if (parameter.GetCustomAttribute<FromQueryAttribute>() != null)
            return BindType.FromQuery;

        if (parameter.GetCustomAttribute<FromBodyAttribute>() != null)
            return BindType.FromBody;

        return null;
    }

    private BoundArguments BindArguments(MethodCallExpression methodExpr, TemplateParameter[] templateParameters)
    {
        var bound = new BoundArguments();

        var args = methodExpr.Method
            .GetParameters()
            .Zip(methodExpr.Arguments)
            .Select(x => new
            {
                BindType = GetBindType(x.First, templateParameters),
                ParameterType = x.First.ParameterType,
                Name = x.First.Name ?? string.Empty,
                Expression = x.Second,
            });

        foreach (var arg in args)
        {
            if (arg.BindType is null)
                continue;

            if (arg.BindType == BindType.FromForm)
            {
                BindFormArgument(bound, arg.Name, arg.Expression);
                continue;
            }

            // ASP.NET Core binds the single [FromBody] parameter to the whole request body, so send the
            // argument's value as the body as-is - serialized with its own JSON contract
            // ([JsonPropertyName], converters), whatever expression produced it.
            if (arg.BindType == BindType.FromBody)
            {
                bound.Body = Expression.Lambda(arg.Expression).Compile().DynamicInvoke();
                bound.HasBody = true;
                continue;
            }

            var target = arg.BindType switch
            {
                BindType.FromPath => bound.Path,
                BindType.FromQuery => bound.Query,
                _ => null
            };

            if (target is null)
                continue;

            AssignValue(target, arg.Name, arg.Expression);
        }

        return bound;
    }

    /// <summary>
    /// Resolves a [FromForm] argument and flattens it into <see cref="BoundArguments.Form"/> /
    /// <see cref="BoundArguments.Files"/>. Mirrors <see cref="AssignValue"/>'s flattening of
    /// MemberInit/Member/Constant expressions, but routes each resolved value (including ones
    /// nested inside a complex form object, e.g. an `IFormFile[]` property) through
    /// <see cref="AddFormProperty"/> so files and arrays are handled correctly instead of
    /// being stringified.
    /// </summary>
    private static void BindFormArgument(BoundArguments bound, string name, Expression expression)
    {
        switch (expression)
        {
            case MemberInitExpression initExpr:
            {
                foreach (var binding in initExpr.Bindings)
                {
                    var assignment = (MemberAssignment)binding;
                    var value = Expression.Lambda(assignment.Expression).Compile().DynamicInvoke();
                    AddFormProperty(bound, assignment.Member.Name, value);
                }

                break;
            }
            case ConstantExpression constExpr:
                AddFormProperty(bound, name, constExpr.Value);
                break;
            case MemberExpression memberExpr:
            {
                var memberValue = Expression.Lambda(memberExpr).Compile().DynamicInvoke();
                var memberType = memberValue?.GetType();
                if (memberType is { IsClass: true } && memberType != typeof(string) && !typeof(IFormFile).IsAssignableFrom(memberType))
                {
                    var properties = memberType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                    foreach (var property in properties)
                        AddFormProperty(bound, property.Name, property.GetValue(memberValue));
                }
                else
                {
                    AddFormProperty(bound, name, memberValue);
                }

                break;
            }
            default:
                AddFormProperty(bound, name, Expression.Lambda(expression).Compile().DynamicInvoke());
                break;
        }
    }

    /// <summary>
    /// Adds a single resolved form value under <paramref name="key"/>, dispatching to the files
    /// dictionary for IFormFile(s), emitting one form entry per element for arrays/collections of
    /// simple values (so the server can bind e.g. Guid[] the same way it binds repeated form
    /// fields), and falling back to a JSON-serialized entry for arrays/collections of complex
    /// objects (e.g. a polymorphic AttributeValue[]), which plain multipart fields can't express.
    /// </summary>
    private static void AddFormProperty(BoundArguments bound, string key, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case IFormFile file:
                bound.Files.GetOrAdd(key).Add(file);
                return;
            case IEnumerable<IFormFile> files:
                bound.Files.GetOrAdd(key).AddRange(files);
                return;
            case string s:
                bound.Form.GetOrAdd(key).Add(s);
                return;
            case System.Collections.IEnumerable enumerable:
            {
                var items = enumerable.Cast<object?>().ToList();
                if (items.Count == 0)
                    return;

                if (items.All(IsSimpleValue))
                {
                    foreach (var item in items)
                        bound.Form.GetOrAdd(key).Add(item?.ToString() ?? string.Empty);
                }
                else if (HasAbstractOrPolymorphicElementType(value))
                {
                    // Default form binding can only construct concrete types with a parameterless
                    // constructor, so it can't materialize e.g. an abstract AttributeValue element
                    // as its correct derived type. Send the whole array as one JSON field instead;
                    // pair this with a [JsonModelBinder] (or similar) on the target property so the
                    // server deserializes it with System.Text.Json, which does understand
                    // [JsonDerivedType]/[JsonPolymorphic].
                    bound.Form.GetOrAdd(key).Add(JsonSerializer.Serialize(value, JsonOptions));
                }
                else
                {
                    // Plain concrete complex elements: use ASP.NET Core's default indexed
                    // form-binding convention (Key[0].Prop=..., Key[1].Prop=...).
                    for (var i = 0; i < items.Count; i++)
                        FlattenIndexedElement(bound, $"{key}[{i}]", items[i]);
                }

                return;
            }
            default:
                bound.Form.GetOrAdd(key).Add(value.ToString() ?? string.Empty);
                return;
        }
    }

    private static bool IsSimpleValue(object? value) =>
        value is null or string or Guid or DateTime or DateTimeOffset or decimal
        || value.GetType().IsPrimitive
        || value.GetType().IsEnum;

    /// <summary>
    /// Determines whether a collection's declared element type is abstract, an interface, or
    /// marked with System.Text.Json polymorphism attributes ([JsonPolymorphic]/[JsonDerivedType]).
    /// Such elements cannot be constructed by the default indexed form binder and should instead
    /// be sent as a single JSON field for a JSON-aware model binder to handle.
    /// </summary>
    private static bool HasAbstractOrPolymorphicElementType(object collection)
    {
        var collectionType = collection.GetType();
        var elementType = collectionType.IsArray
            ? collectionType.GetElementType()
            : collectionType.GetInterfaces()
                .Append(collectionType)
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(i => i.GetGenericArguments()[0])
                .FirstOrDefault();

        if (elementType is null)
            return false;

        return elementType.IsAbstract
            || elementType.IsInterface
            || elementType.GetCustomAttribute<JsonPolymorphicAttribute>() != null;
    }

    /// <summary>
    /// Flattens a single complex element of an array/collection property under an indexed key
    /// prefix (e.g. "AttributeValues[0]"), matching ASP.NET Core's default model-binding
    /// convention of "Key[i].PropertyName" for collections of complex objects submitted as form data.
    /// </summary>
    private static void FlattenIndexedElement(BoundArguments bound, string indexedKey, object? element)
    {
        if (element is null)
            return;

        if (element is IFormFile file)
        {
            bound.Files.GetOrAdd(indexedKey).Add(file);
            return;
        }

        var elementType = element.GetType();
        if (IsSimpleValue(element))
        {
            bound.Form.GetOrAdd(indexedKey).Add(element.ToString() ?? string.Empty);
            return;
        }

        var properties = elementType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in properties)
        {
            AddFormProperty(bound, $"{indexedKey}.{property.Name}", property.GetValue(element));
        }
    }

    /// <summary>
    /// Resolves the value of an argument expression and assigns it (potentially flattening complex objects)
    /// into the target dictionary, preserving the original binding behaviour.
    /// </summary>
    private static void AssignValue(Dictionary<string, object?> target, string name, Expression expression)
    {
        switch (expression)
        {
            case MemberInitExpression initExpr:
            {
                foreach (var binding in initExpr.Bindings)
                {
                    var assignment = (MemberAssignment)binding;
                    var value = Expression.Lambda(assignment.Expression).Compile().DynamicInvoke();
                    target[assignment.Member.Name] = value;
                }

                break;
            }
            case ConstantExpression constExpr:
                target[name] = constExpr.Value;
                break;
            case MemberExpression memberExpr:
            {
                var memberValue = Expression.Lambda(memberExpr).Compile().DynamicInvoke();
                var memberType = memberValue?.GetType();
                if (memberType is { IsClass: true } && memberType != typeof(string))
                {
                    var properties = memberType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                    foreach (var property in properties)
                        target[property.Name] = property.GetValue(memberValue);
                }
                else
                {
                    target[name] = memberValue;
                }

                break;
            }
            default:
                target[name] = Expression.Lambda(expression).Compile().DynamicInvoke();
                break;
        }
    }

    private static string BuildPath(
        string controllerPath,
        HttpMethodAttribute httpAttribute,
        TemplateParameter[] templateParameters,
        BoundArguments boundArguments)
    {
        var fullPath = controllerPath + (httpAttribute.Template is not null ? $"/{httpAttribute.Template}" : string.Empty);

        var queryParts = boundArguments.Query.SelectMany(BuildQueryParameterParts).ToArray();
        if (queryParts.Length > 0)
            fullPath += "?" + string.Join("&", queryParts);

        foreach (var pathParameter in boundArguments.Path)
        {
            var templateParameter = templateParameters.First(x => x.Name == pathParameter.Key);
            fullPath = fullPath.Replace(templateParameter.RoutePattern, pathParameter.Value!.ToString());
        }

        return fullPath;
    }

    /// <summary>
    /// Turns a single bound query argument into one or more "key=value" parts, repeating the key
    /// for each element when the value is an array/collection (matching ASP.NET Core's model
    /// binding convention for e.g. <c>[FromQuery] EpicStatus[]</c>). Null values are omitted so a
    /// default/omitted argument doesn't produce a query param at all.
    /// </summary>
    private static IEnumerable<string> BuildQueryParameterParts(KeyValuePair<string, object?> parameter)
    {
        if (parameter.Value is null)
            return [];

        if (parameter.Value is not string && parameter.Value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object?>().Select(v => $"{parameter.Key}={v}");

        return [$"{parameter.Key}={parameter.Value}"];
    }

    /// <summary>
    /// Builds the request content. When form fields or form files are present a multipart/form-data
    /// payload is produced, otherwise the body is serialized as JSON, matching the previous behaviour.
    /// </summary>
    private static (HttpContent Content, string Description) BuildRequestContent(BoundArguments boundArguments)
    {
        if (boundArguments.Form.Any() || boundArguments.Files.Any())
        {
            var multipartContent = new MultipartFormDataContent();

            foreach (var (fieldName, values) in boundArguments.Form)
            {
                foreach (var value in values)
                {
                    multipartContent.Add(new StringContent(value?.ToString() ?? string.Empty), fieldName);
                }
            }

            foreach (var (fieldName, formFiles) in boundArguments.Files)
            {
                foreach (var formFile in formFiles)
                {
                    var fileContent = new StreamContent(formFile.OpenReadStream());
                    if (!string.IsNullOrEmpty(formFile.ContentType))
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(formFile.ContentType);

                    multipartContent.Add(fileContent, fieldName, formFile.FileName);
                }
            }

            var description = $"[multipart/form-data] fields: {string.Join(", ", boundArguments.Form.Keys)}; files: {string.Join(", ", boundArguments.Files.Select(x => $"{x.Key} ({x.Value.Count})"))}";
            return (multipartContent, description);
        }

        var bodyString = boundArguments.HasBody
            ? JsonSerializer.Serialize(boundArguments.Body, boundArguments.Body?.GetType() ?? typeof(object), JsonOptions)
            : "{}";
        return (new StringContent(bodyString, Encoding.UTF8, "application/json"), bodyString);
    }

    private Task<HttpResponseMessage> SendRequest(HttpMethodAttribute httpAttribute, string fullPath, HttpContent content)
    {
        return httpAttribute switch
        {
            HttpGetAttribute => client.GetAsync(fullPath),
            HttpPostAttribute => client.PostAsync(fullPath, content),
            HttpPutAttribute => client.PutAsync(fullPath, content),
            HttpDeleteAttribute => client.DeleteAsync(fullPath),
            _ => throw new InvalidOperationException($"Requests with {httpAttribute} are not supported")
        };
    }

    private static TemplateParameter[] GetTemplateParameters(string? template)
    {
        if (template == null)
            return [];

        var matches = TemplateParameterRegex.Matches(template);
        return matches
            .Select(x => new TemplateParameter
            {
                Name = x.Groups[1].Value,
                RoutePattern = x.Groups[0].Value,
            })
            .ToArray();
    }

    private record TemplateParameter
    {
        public required string Name { get; set; }
        public required string RoutePattern { get; set; }
    }

    private sealed class BoundArguments
    {
        public Dictionary<string, object?> Query { get; } = new();
        public object? Body { get; set; }
        public bool HasBody { get; set; }
        public Dictionary<string, object?> Path { get; } = new();
        public Dictionary<string, List<object?>> Form { get; } = new();
        public Dictionary<string, List<IFormFile>> Files { get; } = new();
    }

    private static Expression<Func<TController, Task>> ConvertToNonGeneric<T>(
        Expression<Func<TController, Task<T>>> expression)
    {
        var parameter = expression.Parameters[0];
        var convertedBody = Expression.Convert(expression.Body, typeof(Task));
        return Expression.Lambda<Func<TController, Task>>(convertedBody, parameter);
    }

    private async Task HandleNonSuccessCode(HttpResponseMessage response, string bodyDescription)
    {
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            var error =
                $"[{response.RequestMessage?.Method}] {response.RequestMessage?.RequestUri} ({response.StatusCode:D}) \nRequest Content: {bodyDescription}\nResponse Content:{responseContent}";

            // E.g. a 401 from the authentication middleware has no body - still report the status code.
            if (string.IsNullOrWhiteSpace(responseContent))
                throw new HttpRequestException(error, null, response.StatusCode);

            ErrorResponse? errorResponse;

            try
            {
                errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseContent, JsonOptions)!;
            }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Undeserializable response was taken: {error}", ex, response.StatusCode);
            }

            Exception? inner = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => new BadRequestException(errorResponse.Errors!),
                HttpStatusCode.NotFound => new NotFoundException(errorResponse.Message),
                HttpStatusCode.Forbidden => new ForbiddenException(errorResponse.Message),
                _ => null
            };

            throw new HttpRequestException(error, inner, response.StatusCode);
        }
    }

    private enum BindType
    {
        FromQuery,
        FromPath,
        FromBody,
        FromForm,
    }
}

public static class DictionaryExtensions
{
    public static List<T> GetOrAdd<TKey, T>(this Dictionary<TKey, List<T>> dictionary, TKey key) where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var list))
        {
            list = [];
            dictionary[key] = list;
        }

        return list;
    }
}
