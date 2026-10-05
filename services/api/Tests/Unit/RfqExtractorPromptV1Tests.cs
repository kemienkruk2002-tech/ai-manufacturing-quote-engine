using QuoteEngine.Application.Ai;

namespace QuoteEngine.UnitTests;

public sealed class RfqExtractorPromptV1Tests
{
    [Fact]
    public void Valid_request_compiles_exact_contract_versions()
    {
        var result = RfqExtractorPromptV1.Compile(Request());

        Assert.True(result.IsValid);
        Assert.Null(result.Code);
        Assert.Equal("rfq-extractor", result.Prompt!.UseCase);
        Assert.Equal("prompt-v1", result.Prompt.PromptVersion);
        Assert.Equal("v1", result.Prompt.SchemaVersion);
    }

    [Fact]
    public void Same_request_one_hundred_times_compiles_identical_prompts()
    {
        var results = Enumerable.Range(0, 100)
            .Select(_ => RfqExtractorPromptV1.Compile(Request()).Prompt!)
            .ToArray();

        Assert.Single(results.Select(prompt => prompt.SystemPrompt).Distinct());
        Assert.Single(results.Select(prompt => prompt.UserPrompt).Distinct());
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("invalid-json")]
    public void Invalid_request_has_stable_code(string kind)
    {
        var request = kind == "blank"
            ? Request() with { ModelId = " " }
            : Request() with { NormalizedInputJson = "{" };

        var result = RfqExtractorPromptV1.Compile(request);

        Assert.False(result.IsValid);
        Assert.Equal("AI_REQUEST_INVALID", result.Code);
        Assert.Null(result.Prompt);
    }

    [Theory]
    [InlineData("use-case")]
    [InlineData("prompt-version")]
    [InlineData("schema-version")]
    public void Unsupported_contract_identifier_is_blocked_without_fallback(string field)
    {
        var request = field switch
        {
            "use-case" => Request() with { UseCase = "other" },
            "prompt-version" => Request() with { PromptVersion = "prompt-v2" },
            _ => Request() with { SchemaVersion = "v2" }
        };

        var result = RfqExtractorPromptV1.Compile(request);

        Assert.False(result.IsValid);
        Assert.Equal("AI_PROMPT_NOT_SUPPORTED", result.Code);
        Assert.Null(result.Prompt);
    }

    [Fact]
    public void System_prompt_contains_required_extraction_rules_and_prohibitions()
    {
        var prompt = RfqExtractorPromptV1.Compile(Request()).Prompt!.SystemPrompt;

        Assert.Contains("EXPLICIT", prompt);
        Assert.Contains("INFERRED", prompt);
        Assert.Contains("MISSING", prompt);
        Assert.Contains("CONFLICT", prompt);
        Assert.Contains("source reference", prompt);
        Assert.Contains("confirmation", prompt);
        Assert.Contains("Never invent missing values", prompt);
        Assert.Contains("prices, costs or manufacturing times", prompt);
    }

    [Fact]
    public void User_prompt_contains_input_exactly_once_and_ends_with_v1_requirement()
    {
        var request = Request();
        var userPrompt = RfqExtractorPromptV1.Compile(request).Prompt!.UserPrompt;

        Assert.Equal($"INPUT_JSON:\n{request.NormalizedInputJson}\n\nReturn only JSON conforming to RFQ_EXTRACTOR/v1.",
            userPrompt);
        Assert.Equal(1, userPrompt.Split(request.NormalizedInputJson, StringSplitOptions.None).Length - 1);
        Assert.EndsWith("Return only JSON conforming to RFQ_EXTRACTOR/v1.", userPrompt);
    }

    [Fact]
    public void Model_changes_no_prompt_content_and_input_changes_only_user_prompt()
    {
        var baseline = RfqExtractorPromptV1.Compile(Request()).Prompt!;
        var otherModel = RfqExtractorPromptV1.Compile(Request() with { ModelId = "model-b" }).Prompt!;
        var otherInput = RfqExtractorPromptV1.Compile(Request() with
        {
            NormalizedInputJson = "{\"part\":\"P-2\"}"
        }).Prompt!;

        Assert.Equal(baseline.SystemPrompt, otherModel.SystemPrompt);
        Assert.Equal(baseline.UserPrompt, otherModel.UserPrompt);
        Assert.Equal(baseline.SystemPrompt, otherInput.SystemPrompt);
        Assert.NotEqual(baseline.UserPrompt, otherInput.UserPrompt);
        Assert.Equal(baseline.UseCase, otherInput.UseCase);
        Assert.Equal(baseline.PromptVersion, otherInput.PromptVersion);
        Assert.Equal(baseline.SchemaVersion, otherInput.SchemaVersion);
    }

    private static AiStructuredRequest Request() => new("rfq-extractor", "model-a", "prompt-v1", "v1",
        "{\"part\":\"P-1\"}");
}
