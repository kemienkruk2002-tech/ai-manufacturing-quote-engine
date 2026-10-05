using System.Net;
using System.Text;
using System.Text.Json;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RfqExtractorSchemaV1Tests
{
    [Fact]
    public void Load_returns_object_with_expected_schema_identity()
    {
        var definition = RfqExtractorSchemaV1.Load();

        Assert.Equal(JsonValueKind.Object, definition.Schema.ValueKind);
        Assert.Equal(RfqExtractorSchemaV1.SchemaName,
            definition.Schema.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString());
        Assert.Equal(RfqExtractorSchemaV1.SchemaVersion,
            definition.Schema.GetProperty("properties").GetProperty("version").GetProperty("const").GetString());
    }

    [Fact]
    public void Embedded_schema_hash_is_lowercase_sha256()
    {
        Assert.Matches("^[0-9a-f]{64}$", RfqExtractorSchemaV1.Load().Sha256);
    }

    [Fact]
    public void One_hundred_loads_return_identical_raw_json_and_hash()
    {
        var definitions = Enumerable.Range(0, 100).Select(_ => RfqExtractorSchemaV1.Load()).ToArray();

        Assert.Single(definitions.Select(definition => definition.RawJson).Distinct());
        Assert.Single(definitions.Select(definition => definition.Sha256).Distinct());
    }

    [Fact]
    public void Raw_json_contains_versioned_production_schema_identity()
    {
        using var document = JsonDocument.Parse(RfqExtractorSchemaV1.Load().RawJson);
        var root = document.RootElement;

        Assert.Equal("rfq-extractor-v1.schema.json", root.GetProperty("$id").GetString());
        Assert.Equal("RFQ_EXTRACTOR",
            root.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString());
        Assert.Equal("v1",
            root.GetProperty("properties").GetProperty("version").GetProperty("const").GetString());
    }

    [Fact]
    public async Task Production_constructor_sends_embedded_schema_with_fake_http_only()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://openai.invalid/") };
        var provider = new OpenAiResponsesProviderV1(client);

        var result = await provider.ExecuteAsync(new("rfq-extractor", "model-exact", "prompt-v1", "v1",
            "{\"part\":\"P-1\"}"));

        Assert.Equal(AiProviderResultStatus.SUCCESS, result.Status);
        Assert.Equal(1, handler.CallCount);
        using var request = JsonDocument.Parse(handler.Body!);
        var schema = request.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
        Assert.Equal("rfq-extractor-v1.schema.json", schema.GetProperty("$id").GetString());
        Assert.Equal("RFQ_EXTRACTOR",
            schema.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString());
        Assert.Equal("v1",
            schema.GetProperty("properties").GetProperty("version").GetProperty("const").GetString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var output = JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[]
                {
                    new
                    {
                        content = new[]
                        {
                            new { type = "output_text", text = "{\"schema\":\"RFQ_EXTRACTOR\",\"version\":\"v1\"}" }
                        }
                    }
                }
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(output, Encoding.UTF8, "application/json")
            };
        }
    }
}
