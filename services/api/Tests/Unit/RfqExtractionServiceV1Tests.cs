using System.Text.Json;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Quoting;

namespace QuoteEngine.UnitTests;

public sealed class RfqExtractionServiceV1Tests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid RfqId = Guid.Parse("90000000-0000-0000-0000-000000000101");

    [Fact]
    public async Task Only_explicitly_selected_file_version_is_materialized()
    {
        var files = new FakeFiles(
            Version("drawing", 1, "a1"),
            Version("drawing", 2, "a2"));
        var materializer = new FakeMaterializer(version =>
            RfqExtractionSourceMaterializationResult.Success($"content-v{version.VersionNo}"));
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();
        var service = Service(files, materializer, provider, executions);

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.COMPLETED, result.Disposition);
        Assert.Equal([("drawing", 1)], files.FindCalls);
        Assert.Equal([1], materializer.MaterializedVersions);
        Assert.Contains("content-v1", provider.LastRequest!.NormalizedInputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("content-v2", provider.LastRequest.NormalizedInputJson, StringComparison.Ordinal);
        Assert.Single(executions.Writes);
    }

    [Fact]
    public async Task Source_selection_order_does_not_change_request_fingerprint()
    {
        var files = new FakeFiles(
            Version("drawing", 1, "a1"),
            Version("email", 3, "b3"));
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();
        var service = Service(
            files,
            new FakeMaterializer(version =>
                RfqExtractionSourceMaterializationResult.Success($"content-{version.LogicalKey}")),
            provider,
            executions);

        var first = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("email", 3, "doc-type"), new("drawing", 1, "doc-type")]));
        var second = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type"), new("email", 3, "doc-type")]));

        Assert.NotNull(first.RequestFingerprint);
        Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(first.RequestFingerprint, executions.Writes[0].RequestFingerprint);
        Assert.Equal(second.RequestFingerprint, executions.Writes[1].RequestFingerprint);
    }

    [Fact]
    public async Task Missing_explicit_source_does_not_fall_back_to_another_version()
    {
        var files = new FakeFiles(Version("drawing", 2, "a2"));
        var materializer = new FakeMaterializer(_ =>
            RfqExtractionSourceMaterializationResult.Success("should-not-run"));
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();

        var result = await Service(files, materializer, provider, executions).ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(RfqExtractionServiceV1.SourceNotFoundCode, result.Code);
        Assert.Null(result.RequestFingerprint);
        Assert.Equal([("drawing", 1)], files.FindCalls);
        Assert.Empty(materializer.MaterializedVersions);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RfqExtractionServiceV1.SourceNotFoundCode, Assert.Single(executions.Writes).Code);
    }

    [Fact]
    public async Task No_selected_sources_is_persisted_as_review_manual_without_provider()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();

        var result = await Service(
            new FakeFiles(),
            new FakeMaterializer(_ => RfqExtractionSourceMaterializationResult.Success("unused")),
            provider,
            executions).ExecuteAsync(new(
                TenantId, RfqId, "model-a", true, []));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(RfqExtractionServiceV1.SourceRequiredCode, result.Code);
        Assert.Null(result.RequestFingerprint);
        Assert.Equal(0, provider.CallCount);
        var stored = Assert.Single(executions.Writes);
        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, stored.Disposition);
        Assert.Equal(RfqExtractionServiceV1.SourceRequiredCode, stored.Code);
    }

    [Fact]
    public async Task Materializer_failure_is_persisted_and_never_calls_provider()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();
        var service = Service(
            new FakeFiles(Version("drawing", 1, "a1")),
            new FakeMaterializer(_ =>
                RfqExtractionSourceMaterializationResult.Failure("TEST_SOURCE_PARSE_FAILED")),
            provider,
            executions);

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal("TEST_SOURCE_PARSE_FAILED", result.Code);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal("TEST_SOURCE_PARSE_FAILED", Assert.Single(executions.Writes).Code);
    }

    [Fact]
    public async Task Persisted_fingerprint_matches_provider_visible_redacted_request()
    {
        var provider = new CapturingProvider(AiProviderResult.Success(ValidJson()));
        var executions = new FakeExecutions();
        var service = Service(
            new FakeFiles(Version("drawing", 1, "a1")),
            new FakeMaterializer(_ => RfqExtractionSourceMaterializationResult.Success("secret rfq text")),
            provider,
            executions,
            new FixedRedactor("""{"sources":[{"content":"REDACTED"}]}"""));

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        var providerFingerprint = AiRequestFingerprintV1.Create(provider.LastRequest!).Fingerprint;
        Assert.NotNull(providerFingerprint);
        Assert.Equal(providerFingerprint, result.RequestFingerprint);
        Assert.Equal(providerFingerprint, Assert.Single(executions.Writes).RequestFingerprint);
    }

    [Fact]
    public async Task Completed_extraction_persists_raw_provider_output_canonical_draft_and_source_lineage()
    {
        var raw = ValidJson();
        var executions = new FakeExecutions();
        var service = Service(
            new FakeFiles(Version("drawing", 1, "a1")),
            new FakeMaterializer(_ => RfqExtractionSourceMaterializationResult.Success("rfq text")),
            new CapturingProvider(AiProviderResult.Success(raw)),
            executions);

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.COMPLETED, result.Disposition);
        var attempt = Assert.Single(executions.AttemptWrites);
        Assert.Equal(raw, attempt.RawProviderOutput);
        Assert.Equal(raw, attempt.CanonicalDraftJson);
        using var lineage = JsonDocument.Parse(attempt.SourceLineageJson);
        var source = Assert.Single(lineage.RootElement.EnumerateArray().ToArray());
        Assert.Equal("drawing", source.GetProperty("logical_key").GetString());
        Assert.Equal(1, source.GetProperty("version_no").GetInt32());
        Assert.Equal("doc-type", source.GetProperty("document_type").GetString());
        Assert.Equal(new string('a', 64), source.GetProperty("sha256").GetString());
        Assert.Equal("source:drawing:1", source.GetProperty("source_reference").GetString());
    }

    [Fact]
    public async Task Invalid_provider_output_is_retained_in_attempt_history_but_never_becomes_current_draft()
    {
        const string raw = """{"unexpected":true}""";
        var executions = new FakeExecutions();
        var service = Service(
            new FakeFiles(Version("drawing", 1, "a1")),
            new FakeMaterializer(_ => RfqExtractionSourceMaterializationResult.Success("rfq text")),
            new CapturingProvider(AiProviderResult.Success(raw)),
            executions);

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", true,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        var attempt = Assert.Single(executions.AttemptWrites);
        Assert.Equal(raw, attempt.RawProviderOutput);
        Assert.Null(attempt.CanonicalDraftJson);
    }

    [Fact]
    public async Task Policy_block_is_persisted_with_deterministic_request_fingerprint()
    {
        var executions = new FakeExecutions();
        var service = Service(
            new FakeFiles(Version("drawing", 1, "a1")),
            new FakeMaterializer(_ => RfqExtractionSourceMaterializationResult.Success("rfq text")),
            new CapturingProvider(AiProviderResult.Success(ValidJson())),
            executions);

        var result = await service.ExecuteAsync(new(
            TenantId, RfqId, "model-a", false,
            [new("drawing", 1, "doc-type")]));

        Assert.Equal(AiExecutionDisposition.REVIEW_MANUAL, result.Disposition);
        Assert.Equal(AiPolicyExecutorV1.ExternalAiNotAllowedCode, result.Code);
        Assert.NotNull(result.RequestFingerprint);
        var write = Assert.Single(executions.Writes);
        Assert.Equal(result.RequestFingerprint, write.RequestFingerprint);
        Assert.Equal("model-a", write.ModelId);
        Assert.Equal(RfqExtractorPromptV1.PromptVersion, write.PromptVersion);
        Assert.Equal(RfqExtractorPromptV1.SchemaVersion, write.SchemaVersion);
    }

    private static RfqExtractionServiceV1 Service(
        FakeFiles files,
        FakeMaterializer materializer,
        CapturingProvider provider,
        FakeExecutions executions,
        IAiInputRedactor? redactor = null)
    {
        var policy = new AiExecutionPolicyV1(
            new HashSet<string>(new[] { RfqExtractorPromptV1.UseCase }, StringComparer.Ordinal),
            new HashSet<string>(new[] { "model-a" }, StringComparer.Ordinal),
            new HashSet<string>(new[] { "doc-type" }, StringComparer.Ordinal),
            64 * 1024);
        var executor = new AiPolicyExecutorV1(
            new AiGatewayV1(provider),
            redactor ?? new PassThroughRedactor(),
            policy);
        return new(files, materializer, executor, executions);
    }

    private static RfqFileVersion Version(string logicalKey, int versionNo, string seed) =>
        new(
            TenantId,
            Guid.NewGuid(),
            RfqId,
            Guid.NewGuid(),
            logicalKey,
            versionNo,
            $"{logicalKey}.txt",
            "text/plain",
            seed.Length,
            new string(seed[0], 64),
            DateTimeOffset.Parse("2026-10-06T08:00:00Z"),
            $"source:{logicalKey}:{versionNo}");

    private static string ValidJson() => JsonSerializer.Serialize(new CanonicalRfqV1(
        Missing<string>(), Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(),
        [], [], [], [], []));

    private static CanonicalRfqFact<T> Missing<T>() => new(
        [], default, [], null, RfqFactClassification.MISSING, false, []);

    private sealed class FixedRedactor(string normalizedJson) : IAiInputRedactor
    {
        public AiRedactionResult Redact(string normalizedInputJson) =>
            AiRedactionResult.Allowed(normalizedJson);
    }

    private sealed class PassThroughRedactor : IAiInputRedactor
    {
        public AiRedactionResult Redact(string normalizedInputJson) =>
            AiRedactionResult.Allowed(normalizedInputJson);
    }

    private sealed class CapturingProvider(AiProviderResult result) : IAiStructuredProvider
    {
        public int CallCount { get; private set; }
        public AiStructuredRequest? LastRequest { get; private set; }

        public Task<AiProviderResult> ExecuteAsync(
            AiStructuredRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeMaterializer(
        Func<RfqFileVersion, RfqExtractionSourceMaterializationResult> resultFactory)
        : IRfqExtractionSourceMaterializer
    {
        public List<int> MaterializedVersions { get; } = [];

        public Task<RfqExtractionSourceMaterializationResult> MaterializeAsync(
            RfqFileVersion source,
            string documentType,
            CancellationToken cancellationToken = default)
        {
            MaterializedVersions.Add(source.VersionNo);
            return Task.FromResult(resultFactory(source));
        }
    }

    private sealed class FakeExecutions : IRfqExtractionExecutionRepository
    {
        public List<RfqExtractionExecutionWrite> Writes { get; } = [];
        public List<RfqExtractionAttemptWrite> AttemptWrites { get; } = [];

        public Task<StoredRfqExtractionExecution> SaveAsync(
            RfqExtractionExecutionWrite write,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(write);
            return Task.FromResult(new StoredRfqExtractionExecution(
                Guid.NewGuid(),
                write.TenantId,
                write.QuoteRequestId,
                write.ModelId,
                write.PromptVersion,
                write.SchemaVersion,
                write.RequestFingerprint,
                write.Disposition,
                write.Code,
                DateTimeOffset.Parse("2026-10-06T08:00:00Z")));
        }

        public Task<RfqExtractionPersistenceResult> SaveAttemptAsync(
            RfqExtractionAttemptWrite write,
            CancellationToken cancellationToken = default)
        {
            AttemptWrites.Add(write);
            var now = DateTimeOffset.Parse("2026-10-06T08:00:00Z");
            var attemptId = Guid.NewGuid();
            var attempt = new StoredRfqExtractionAttempt(
                attemptId,
                write.TenantId,
                write.QuoteRequestId,
                write.ModelId,
                write.PromptVersion,
                write.SchemaVersion,
                write.RequestFingerprint,
                write.Disposition,
                write.Code,
                write.SourceLineageJson,
                write.RawProviderOutput,
                now);
            StoredRfqCanonicalDraft? draft = write.CanonicalDraftJson is null ? null
                : new(write.TenantId, write.QuoteRequestId, attemptId, write.CanonicalDraftJson, 1, now, now);
            return Task.FromResult(new RfqExtractionPersistenceResult(attempt, draft));
        }

        public Task<IReadOnlyList<StoredRfqExtractionAttempt>> ListAttemptsAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredRfqExtractionAttempt>>([]);

        public Task<StoredRfqCanonicalDraft?> FindCurrentDraftAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredRfqCanonicalDraft?>(null);
    }

    private sealed class FakeFiles(params RfqFileVersion[] versions) : IRfqFileRepository
    {
        private readonly IReadOnlyList<RfqFileVersion> versions = versions;
        public List<(string LogicalKey, int VersionNo)> FindCalls { get; } = [];

        public Task<bool> RequestExistsAsync(
            Guid tenantId,
            Guid quoteRequestId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == TenantId && quoteRequestId == RfqId);

        public Task<RfqFileVersion> GetOrCreateVersionAsync(
            RfqFileVersionInput input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RfqFileVersion?> FindVersionAsync(
            Guid tenantId,
            Guid quoteRequestId,
            string logicalKey,
            int versionNo,
            CancellationToken cancellationToken = default)
        {
            FindCalls.Add((logicalKey, versionNo));
            return Task.FromResult(versions.SingleOrDefault(version =>
                version.TenantId == tenantId
                && version.QuoteRequestId == quoteRequestId
                && StringComparer.Ordinal.Equals(version.LogicalKey, logicalKey)
                && version.VersionNo == versionNo));
        }

        public Task<IReadOnlyList<RfqFileVersion>> ListVersionsAsync(
            Guid tenantId,
            Guid quoteRequestId,
            string logicalKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
