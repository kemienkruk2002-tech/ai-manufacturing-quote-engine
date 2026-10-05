using System.Net;
using System.Text;
using System.Text.Json;
using QuoteEngine.Application.Ai;

namespace QuoteEngine.UnitTests;

public sealed class OpenAiResponsesProviderV1Tests
{
    private const string RawRfqJson = "{\"schema\":\"RFQ_EXTRACTOR\",\"version\":\"v1\"}";
    private const string SchemaJson = "{\"type\":\"object\",\"properties\":{\"schema\":{\"const\":\"RFQ_EXTRACTOR\"}},\"required\":[\"schema\"],\"additionalProperties\":false}";

    [Theory]
    [InlineData("invalid", "AI_REQUEST_INVALID")]
    [InlineData("unsupported", "AI_PROMPT_NOT_SUPPORTED")]
    public async Task Compile_failure_preserves_code_and_does_not_call_http(string kind, string expectedCode)
    {
        var handler = Handler(_ => CompletedResponse());
        using var client = Client(handler);
        var request = kind == "invalid"
            ? Request() with { ModelId = " " }
            : Request() with { PromptVersion = "prompt-v2" };

        var result = await Provider(client).ExecuteAsync(request);

        AssertFailure(result, AiProviderFailureKind.PERMANENT, expectedCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Valid_request_posts_exact_minimal_structured_output_body()
    {
        var handler = Handler(_ => CompletedResponse());
        using var client = Client(handler);
        var request = Request();
        var compiled = RfqExtractorPromptV1.Compile(request).Prompt!;

        await Provider(client).ExecuteAsync(request);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Methods.Single());
        Assert.Equal("/v1/responses", handler.Paths.Single());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var root = body.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal(request.ModelId, root.GetProperty("model").GetString());
        Assert.Equal(compiled.SystemPrompt, root.GetProperty("instructions").GetString());
        Assert.Equal(compiled.UserPrompt, root.GetProperty("input").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal(4, format.EnumerateObject().Count());
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("rfq_extractor_v1", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(SchemaJson, format.GetProperty("schema").GetRawText());
    }

    [Fact]
    public async Task Completed_response_with_single_output_text_returns_raw_json_unchanged()
    {
        var handler = Handler(_ => CompletedResponse());
        using var client = Client(handler);

        var result = await Provider(client).ExecuteAsync(Request());

        Assert.Equal(AiProviderResultStatus.SUCCESS, result.Status);
        Assert.Equal(RawRfqJson, result.RawJson);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Transient_http_status_is_mapped(HttpStatusCode status)
    {
        var handler = Handler(_ => new HttpResponseMessage(status));
        using var client = Client(handler);

        var result = await Provider(client).ExecuteAsync(Request());

        AssertFailure(result, AiProviderFailureKind.TRANSIENT, "OPENAI_HTTP_TRANSIENT");
    }

    [Fact]
    public async Task Permanent_http_status_is_mapped()
    {
        var handler = Handler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = Client(handler);

        var result = await Provider(client).ExecuteAsync(Request());

        AssertFailure(result, AiProviderFailureKind.PERMANENT, "OPENAI_HTTP_PERMANENT");
    }

    [Fact]
    public async Task Http_request_exception_is_mapped_as_transport_error()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline failure")));
        using var client = Client(handler);

        var result = await Provider(client).ExecuteAsync(Request());

        AssertFailure(result, AiProviderFailureKind.TRANSIENT, "OPENAI_TRANSPORT_ERROR");
    }

    [Fact]
    public async Task Caller_cancellation_is_mapped()
    {
        var handler = new FakeHttpMessageHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token));
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await Provider(client).ExecuteAsync(Request(), cancellation.Token);

        AssertFailure(result, AiProviderFailureKind.CANCELLED, "OPENAI_CANCELLED");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("no-output")]
    [InlineData("multiple-output")]
    public async Task Invalid_success_response_is_mapped_as_permanent(string kind)
    {
        var responseBody = kind switch
        {
            "malformed" => "{",
            "no-output" => "{\"status\":\"completed\",\"output\":[]}",
            _ => ResponseJson(RawRfqJson, RawRfqJson)
        };
        var handler = Handler(_ => JsonResponse(responseBody));
        using var client = Client(handler);

        var result = await Provider(client).ExecuteAsync(Request());

        AssertFailure(result, AiProviderFailureKind.PERMANENT, "OPENAI_RESPONSE_INVALID");
    }

    [Fact]
    public async Task Same_request_one_hundred_times_has_identical_body_and_result()
    {
        var handler = Handler(_ => CompletedResponse());
        using var client = Client(handler);
        var provider = Provider(client);
        var results = new List<AiProviderResult>();
        for (var index = 0; index < 100; index++) results.Add(await provider.ExecuteAsync(Request()));

        Assert.Equal(100, handler.CallCount);
        Assert.Single(handler.Bodies.Distinct());
        Assert.Single(results.Select(result => result.Status).Distinct());
        Assert.Single(results.Select(result => result.RawJson).Distinct());
        Assert.All(results, result => Assert.Equal(AiProviderResultStatus.SUCCESS, result.Status));
        Assert.All(results, result => Assert.Equal(RawRfqJson, result.RawJson));
    }

    private static OpenAiResponsesProviderV1 Provider(HttpClient client)
    {
        using var schema = JsonDocument.Parse(SchemaJson);
        return new(client, schema.RootElement);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://openai.invalid/")
    };

    private static FakeHttpMessageHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new((request, _) => Task.FromResult(response(request)));

    private static AiStructuredRequest Request() => new("rfq-extractor", "model-exact", "prompt-v1", "v1",
        "{\"part\":\"P-1\"}");

    private static HttpResponseMessage CompletedResponse() => JsonResponse(ResponseJson(RawRfqJson));

    private static string ResponseJson(params string[] outputTexts) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[]
        {
            new
            {
                type = "message",
                content = outputTexts.Select(text => new { type = "output_text", text }).ToArray()
            }
        }
    });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void AssertFailure(AiProviderResult result, AiProviderFailureKind kind, string code)
    {
        Assert.Equal(AiProviderResultStatus.FAILURE, result.Status);
        Assert.Equal(kind, result.FailureKind);
        Assert.Equal(code, result.FailureCode);
        Assert.Null(result.RawJson);
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return await response(request, cancellationToken);
        }
    }
}
