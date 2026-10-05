using System.Net;
using System.Text;
using System.Text.Json;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.Application.Ai;

public sealed class OpenAiResponsesProviderV1 : IAiStructuredProvider
{
    public const string HttpTransientCode = "OPENAI_HTTP_TRANSIENT";
    public const string TransportErrorCode = "OPENAI_TRANSPORT_ERROR";
    public const string HttpPermanentCode = "OPENAI_HTTP_PERMANENT";
    public const string CancelledCode = "OPENAI_CANCELLED";
    public const string ResponseInvalidCode = "OPENAI_RESPONSE_INVALID";

    private readonly HttpClient httpClient;
    private readonly JsonElement rfqSchema;

    public OpenAiResponsesProviderV1(HttpClient httpClient)
        : this(httpClient, RfqExtractorSchemaV1.Load().Schema) { }

    public OpenAiResponsesProviderV1(HttpClient httpClient, JsonElement rfqSchema)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.rfqSchema = rfqSchema.Clone();
    }

    public async Task<AiProviderResult> ExecuteAsync(AiStructuredRequest request,
        CancellationToken cancellationToken = default)
    {
        var compilation = RfqExtractorPromptV1.Compile(request);
        if (!compilation.IsValid)
            return AiProviderResult.Failure(AiProviderFailureKind.PERMANENT, compilation.Code!);

        var prompt = compilation.Prompt!;
        var payload = JsonSerializer.Serialize(new
        {
            model = request.ModelId,
            instructions = prompt.SystemPrompt,
            input = prompt.UserPrompt,
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "rfq_extractor_v1",
                    strict = true,
                    schema = rfqSchema
                }
            }
        });

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
                return AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, HttpTransientCode);
            if (!response.IsSuccessStatusCode)
                return AiProviderResult.Failure(AiProviderFailureKind.PERMANENT, HttpPermanentCode);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseCompletedResponse(responseJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AiProviderResult.Failure(AiProviderFailureKind.CANCELLED, CancelledCode);
        }
        catch (OperationCanceledException)
        {
            return AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, TransportErrorCode);
        }
        catch (HttpRequestException)
        {
            return AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, TransportErrorCode);
        }
        catch (IOException)
        {
            return AiProviderResult.Failure(AiProviderFailureKind.TRANSIENT, TransportErrorCode);
        }
    }

    private static AiProviderResult ParseCompletedResponse(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.String
                || !StringComparer.Ordinal.Equals(status.GetString(), "completed")
                || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
                return InvalidResponse();

            var outputTextCount = 0;
            string? rawJson = null;
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object
                        || !part.TryGetProperty("type", out var type)
                        || type.ValueKind != JsonValueKind.String
                        || !StringComparer.Ordinal.Equals(type.GetString(), "output_text")) continue;
                    outputTextCount++;
                    rawJson = part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                        ? text.GetString() : null;
                }
            }

            return outputTextCount == 1 && !string.IsNullOrWhiteSpace(rawJson)
                ? AiProviderResult.Success(rawJson)
                : InvalidResponse();
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private static AiProviderResult InvalidResponse() =>
        AiProviderResult.Failure(AiProviderFailureKind.PERMANENT, ResponseInvalidCode);
}
