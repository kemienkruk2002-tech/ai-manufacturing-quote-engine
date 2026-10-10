using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using QuoteEngine.Application;
using QuoteEngine.Application.Ai;
using QuoteEngine.Domain.Calculation;
using QuoteEngine.Domain.Quoting;
using QuoteEngine.Persistence;

namespace QuoteEngine.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RfqKeyedExtractionPersistenceTests(PostgresFixture db)
{
    private const string V1 = RfqExtractionIdempotencyV1.Version;
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private RfqExtractionExecutionRepository Repository => new(db.DataSource);

    [Fact]
    public async Task First_keyed_completed_save_persists_identity_audit_link_and_draft()
    {
        var rfq = await CreateRfqAsync();
        var identity = Key("first");
        var result = await SaveAsync(rfq, identity);
        Assert.NotNull(result.Persistence.CurrentDraft);
        Assert.Equal(result.Persistence.Attempt.Id, result.Persistence.CurrentDraft!.SourceAttemptId);
        Assert.Equal(1, result.Persistence.CurrentDraft.RowVersion);
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT idempotency_version,idempotency_key,idempotency_request_hash,audit_event_id
            FROM rfq_extraction_attempts WHERE id=@id
            """);
        cmd.Parameters.AddWithValue("id", result.Persistence.Attempt.Id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(identity.Version, reader.GetString(0));
        Assert.Equal(identity.Key, reader.GetString(1));
        Assert.Equal(identity.RequestHash, reader.GetString(2).TrimEnd(' '));
        Assert.Equal(result.Execution.AuditEventId, reader.GetGuid(3));
        Assert.Equal(PostgresFixture.TenantId, result.Execution.TenantId);
    }

    [Fact]
    public async Task Sequential_replay_returns_original_rows_and_does_not_mutate_current_draft()
    {
        var rfq = await CreateRfqAsync();
        var first = await SaveAsync(rfq, Key("repeat"), canonical: Canonical("ORIGINAL"), raw: "original");
        var before = (await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfq))!;
        var replay = await SaveAsync(rfq, Key("repeat"), canonical: Canonical("LOSER"), raw: "loser");
        Assert.Equal(first.Execution, replay.Execution);
        Assert.Equal(first.Persistence.Attempt, replay.Persistence.Attempt);
        Assert.Equal("original", replay.Persistence.Attempt.RawProviderOutput);
        Assert.Equal(first.Persistence.CurrentDraft, replay.Persistence.CurrentDraft);
        Assert.Equal(before, replay.Persistence.CurrentDraft);
        Assert.Equal(before, await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfq));
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.Empty(await new RfqCanonicalReviewRepository(db.DataSource).ListAsync(PostgresFixture.TenantId, rfq));
    }

    [Fact]
    public async Task Replay_preserves_human_correction_and_review_history()
    {
        var rfq = await CreateRfqAsync();
        var first = await SaveAsync(rfq, Key("review"), canonical: Canonical("INITIAL"));
        var reviewed = Canonical("HUMAN-CORRECTED");
        var beforeFact = JsonSerializer.Serialize(Missing<string>());
        var result = await new RfqCanonicalReviewRepository(db.DataSource).ApplyAsync(new(
            PostgresFixture.TenantId, rfq, "rfq_number", RfqCanonicalReviewDecision.CORRECT,
            reviewed, beforeFact, beforeFact, 1, "human-reviewer", "source-file",
            "Verified by a human.", null));
        Assert.Equal(RfqCanonicalReviewApplyStatus.UPDATED, result.Status);
        var replay = await SaveAsync(rfq, Key("review"), canonical: Canonical("SHOULD-NOT-APPLY"));
        Assert.Equal(first.Execution, replay.Execution);
        Assert.Equal(first.Persistence.Attempt, replay.Persistence.Attempt);
        Assert.Equal(result.Draft, replay.Persistence.CurrentDraft);
        Assert.Equal(2, replay.Persistence.CurrentDraft!.RowVersion);
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.Single(await new RfqCanonicalReviewRepository(db.DataSource).ListAsync(PostgresFixture.TenantId, rfq));
    }

    [Fact]
    public async Task Replay_after_different_key_preserves_newer_attempt_draft()
    {
        var rfq = await CreateRfqAsync();
        var first = await SaveAsync(rfq, Key("old"), canonical: Canonical("OLD"));
        var newer = await SaveAsync(rfq, Key("new"), canonical: Canonical("NEW"));
        var replay = await SaveAsync(rfq, Key("old"), canonical: Canonical("WRONG"));
        Assert.Equal(first.Persistence.Attempt.Id, replay.Persistence.Attempt.Id);
        Assert.Equal(newer.Persistence.CurrentDraft, replay.Persistence.CurrentDraft);
        Assert.Equal(newer.Persistence.Attempt.Id, replay.Persistence.CurrentDraft!.SourceAttemptId);
        Assert.Equal(2L, await AttemptsAsync(rfq));
        Assert.Equal(2L, await AuditsAsync(rfq));
    }

    [Fact]
    public async Task Review_manual_without_draft_is_idempotent_and_retains_null_payload()
    {
        var rfq = await CreateRfqAsync();
        var identity = Key("manual");
        var first = await SaveAsync(rfq, identity, AiExecutionDisposition.REVIEW_MANUAL, null, "provider-1", "REVIEW");
        var replay = await SaveAsync(rfq, identity, AiExecutionDisposition.REVIEW_MANUAL, null, "provider-2", "REVIEW");
        Assert.Equal(first, replay);
        Assert.Null(replay.Persistence.CurrentDraft);
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
    }

    [Fact]
    public async Task Conflict_and_lookup_are_read_only_and_tenant_scoped()
    {
        var rfq = await CreateRfqAsync();
        var identity = Key("same-key");
        var first = await SaveAsync(rfq, identity, raw: "original");
        var found = await Repository.FindKeyedAsync(PostgresFixture.TenantId, rfq, identity);
        Assert.Equal(first, found);
        Assert.Null(await Repository.FindKeyedAsync(PostgresFixture.TenantId, rfq, Key("missing")));
        Assert.Null(await Repository.FindKeyedAsync(Guid.NewGuid(), rfq, identity));
        Assert.Null(await Repository.FindKeyedAsync(PostgresFixture.TenantId, Guid.NewGuid(), identity));
        var changed = identity with { RequestHash = HashB };
        await AssertConflictAsync(() => Repository.FindKeyedAsync(PostgresFixture.TenantId, rfq, changed));
        await AssertConflictAsync(() => SaveAsync(rfq, changed, raw: "different"));
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.Equal(first.Persistence.CurrentDraft, await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfq));
    }

    [Fact]
    public async Task Distinct_versions_keys_rfqs_and_tenants_are_independent_and_unkeyed_is_legacy()
    {
        var rfq = await CreateRfqAsync();
        var anotherRfq = await CreateRfqAsync();
        var otherTenant = await CreateTenantAsync();
        var foreignRfq = await CreateRfqAsync(otherTenant);
        var a = await SaveAsync(rfq, Key("same"));
        var b = await SaveAsync(rfq, Key("other"));
        var c = await SaveAsync(rfq, Key("same") with { Version = "future-version" });
        var d = await SaveAsync(anotherRfq, Key("same"));
        var e = await SaveAsync(foreignRfq, Key("same"), tenant: otherTenant);
        Assert.Equal(3L, await AttemptsAsync(rfq));
        Assert.Equal(3L, await AuditsAsync(rfq));
        Assert.Equal(3, new[] { a, b, c }.Select(x => x.Persistence.Attempt.Id).Distinct().Count());
        Assert.NotEqual(d.Persistence.Attempt.Id, e.Persistence.Attempt.Id);

        var (execution, attempt) = Writes(rfq, PostgresFixture.TenantId, AiExecutionDisposition.REVIEW_MANUAL, null, "unkeyed", "MANUAL");
        await Repository.SaveAtomicAsync(execution, attempt);
        await Repository.SaveAtomicAsync(execution, attempt);
        Assert.Equal(5L, await AttemptsAsync(rfq));
        Assert.Equal(5L, await AuditsAsync(rfq));
        Assert.Equal(a, await Repository.FindKeyedAsync(PostgresFixture.TenantId, rfq, Key("same")));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("bad\nkey")]
    public async Task Invalid_key_is_rejected_before_any_write(string key)
    {
        var rfq = await CreateRfqAsync();
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(rfq, Key(key)));
        Assert.Contains(error.Errors, item => item.Code == "RFQ_EXTRACTION_IDEMPOTENCY_KEY_INVALID");
        Assert.Equal(0L, await AuditsAsync(rfq));
        Assert.Equal(0L, await AttemptsAsync(rfq));
    }

    [Fact]
    public async Task Invalid_hash_and_atomic_mismatch_do_not_write()
    {
        var rfq = await CreateRfqAsync();
        var invalid = Key("invalid") with { RequestHash = HashA.ToUpperInvariant() };
        var hashError = await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(rfq, invalid));
        Assert.Contains(hashError.Errors, e => e.Code == "RFQ_EXTRACTION_IDEMPOTENCY_IDENTITY_INVALID");
        var (execution, attempt) = Writes(rfq, PostgresFixture.TenantId, AiExecutionDisposition.REVIEW_MANUAL, null, null, "MANUAL");
        var mismatch = attempt with { ModelId = "different" };
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Repository.SaveKeyedAtomicAsync(execution, mismatch, Key("mismatch")));
        Assert.Contains(error.Errors, e => e.Code == "RFQ_EXTRACTION_ATOMIC_WRITE_MISMATCH");
        Assert.Equal(0L, await AttemptsAsync(rfq));
        Assert.Equal(0L, await AuditsAsync(rfq));
    }

    [Fact]
    public async Task Keyed_save_failure_rolls_back_audit_attempt_and_allows_later_retry()
    {
        var rfq = await CreateRfqAsync();
        var key = Key("rollback");
        await using (var command = db.DataSource.CreateCommand("""
            ALTER TABLE rfq_canonical_drafts
            ADD CONSTRAINT issue_37_forced_failure CHECK (false) NOT VALID
            """))
            await command.ExecuteNonQueryAsync();
        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(rfq, key));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal(0L, await AttemptsAsync(rfq));
            Assert.Equal(0L, await AuditsAsync(rfq));
        }
        finally
        {
            await using var command = db.DataSource.CreateCommand("""
                ALTER TABLE rfq_canonical_drafts
                DROP CONSTRAINT IF EXISTS issue_37_forced_failure
                """);
            await command.ExecuteNonQueryAsync();
        }
        var retried = await SaveAsync(rfq, key);
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.NotNull(retried.Persistence.CurrentDraft);
    }

    [Fact]
    public async Task Concurrent_equal_hash_calls_on_separate_connections_commit_once()
    {
        var rfq = await CreateRfqAsync();
        var key = Key("concurrent-equal");
        await using var holder = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await HoldScopeLockAsync(holder, transaction, rfq, key);
        var first = Task.Run(() => SaveAsync(rfq, key, canonical: Canonical("ONE"), raw: "first"));
        var second = Task.Run(() => SaveAsync(rfq, key, canonical: Canonical("TWO"), raw: "second"));
        await WaitForLockWaitersAsync(2);
        await transaction.CommitAsync();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(outcomes[0].Execution.AuditEventId, outcomes[1].Execution.AuditEventId);
        Assert.Equal(outcomes[0].Persistence.Attempt.Id, outcomes[1].Persistence.Attempt.Id);
        Assert.Equal(outcomes[0].Persistence.Attempt.RawProviderOutput, outcomes[1].Persistence.Attempt.RawProviderOutput);
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.Equal(1L, (await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfq))!.RowVersion);
    }

    [Fact]
    public async Task Concurrent_different_hash_calls_commit_one_winner_and_one_conflict()
    {
        var rfq = await CreateRfqAsync();
        var key = Key("concurrent-conflict");
        await using var holder = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await HoldScopeLockAsync(holder, transaction, rfq, key);
        var first = Task.Run(async () => await CaptureAsync(() => SaveAsync(rfq, key, raw: "A")));
        var second = Task.Run(async () => await CaptureAsync(() =>
            SaveAsync(rfq, key with { RequestHash = HashB }, raw: "B")));
        await WaitForLockWaitersAsync(2);
        await transaction.CommitAsync();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(outcomes, x => x.Result is not null);
        var failure = Assert.Single(outcomes.Where(x => x.Error is not null)).Error;
        Assert.Contains(failure!.Errors, e => e.Code == "RFQ_EXTRACTION_IDEMPOTENCY_CONFLICT");
        Assert.Equal(1L, await AttemptsAsync(rfq));
        Assert.Equal(1L, await AuditsAsync(rfq));
        Assert.Equal(1L, (await Repository.FindCurrentDraftAsync(PostgresFixture.TenantId, rfq))!.RowVersion);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_scope_lock_rolls_back_and_releases_resources()
    {
        var rfq = await CreateRfqAsync();
        var key = Key("cancel");
        await using var holder = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await HoldScopeLockAsync(holder, transaction, rfq, key);
        using var cts = new CancellationTokenSource();
        var (execution, attempt) = Writes(rfq, PostgresFixture.TenantId, AiExecutionDisposition.COMPLETED, Canonical("CANCEL"), "raw", null);
        var pending = Repository.SaveKeyedAtomicAsync(execution, attempt, key, cts.Token);
        await WaitForLockWaitersAsync(1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0L, await AttemptsAsync(rfq));
        Assert.Equal(0L, await AuditsAsync(rfq));
        await transaction.CommitAsync();
        var success = await SaveAsync(rfq, key);
        Assert.NotNull(success.Persistence.CurrentDraft);
        Assert.Equal(1L, await AttemptsAsync(rfq));
    }

    private static async Task<(RfqExtractionAtomicPersistenceResult? Result, DomainValidationException? Error)> CaptureAsync(
        Func<Task<RfqExtractionAtomicPersistenceResult>> callback)
    {
        try { return (await callback(), null); }
        catch (DomainValidationException error) { return (null, error); }
    }

    private static async Task AssertConflictAsync(Func<Task> callback)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(callback);
        Assert.Contains(error.Errors, e => e.Code == "RFQ_EXTRACTION_IDEMPOTENCY_CONFLICT");
    }

    private async Task WaitForLockWaitersAsync(int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await using var command = db.DataSource.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname=current_database()
                  AND wait_event_type='Lock'
                  AND query ILIKE '%pg_advisory_xact_lock%'
                """);
            if ((long)(await command.ExecuteScalarAsync(timeout.Token))! >= expected) return;
            await Task.Delay(20, timeout.Token); // Poll for a DB-observed barrier, not a timing race.
        }
    }

    private static async Task HoldScopeLockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid rfq, RfqExtractionIdempotencyIdentity identity)
    {
        var scope = JsonSerializer.Serialize(new[]
        {
            PostgresFixture.TenantId.ToString("D"), rfq.ToString("D"), identity.Version, identity.Key
        });
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        var lockId = BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lockId)", connection, transaction);
        command.Parameters.AddWithValue("lockId", lockId);
        await command.ExecuteNonQueryAsync();
    }

    private static RfqExtractionIdempotencyIdentity Key(string key) => new(V1, key, HashA);

    private async Task<RfqExtractionAtomicPersistenceResult> SaveAsync(Guid rfq,
        RfqExtractionIdempotencyIdentity key, AiExecutionDisposition disposition = AiExecutionDisposition.COMPLETED,
        string? canonical = null, string? raw = "provider", string? code = null, Guid? tenant = null)
    {
        var (execution, attempt) = Writes(rfq, tenant ?? PostgresFixture.TenantId, disposition,
            disposition == AiExecutionDisposition.COMPLETED ? canonical ?? Canonical("DEFAULT") : null, raw, code);
        return await Repository.SaveKeyedAtomicAsync(execution, attempt, key);
    }

    private static (RfqExtractionExecutionWrite, RfqExtractionAttemptWrite) Writes(
        Guid rfq, Guid tenant, AiExecutionDisposition disposition, string? canonical, string? raw, string? code)
    {
        var execution = new RfqExtractionExecutionWrite(tenant, rfq, "model-test",
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion,
            HashA, disposition, code);
        var attempt = new RfqExtractionAttemptWrite(tenant, rfq, "model-test",
            RfqExtractorPromptV1.PromptVersion, RfqExtractorPromptV1.SchemaVersion,
            HashA, disposition, code, "[]", raw, canonical);
        return (execution, attempt);
    }

    private async Task<Guid> CreateRfqAsync(Guid? tenant = null)
    {
        var rfq = Guid.NewGuid();
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO quote_requests(id,tenant_id,status,currency)
            VALUES(@rfq,@tenant,'New','PLN')
            """);
        cmd.Parameters.AddWithValue("rfq", rfq);
        cmd.Parameters.AddWithValue("tenant", tenant ?? PostgresFixture.TenantId);
        await cmd.ExecuteNonQueryAsync();
        return rfq;
    }

    private async Task<Guid> CreateTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var command = db.DataSource.CreateCommand("INSERT INTO tenants(id,name) VALUES(@id,@name)");
        command.Parameters.AddWithValue("id", tenant);
        command.Parameters.AddWithValue("name", "Issue37_" + tenant.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return tenant;
    }

    private Task<long> AttemptsAsync(Guid rfq) => db.ScalarAsync<long>(
        $"SELECT count(*) FROM rfq_extraction_attempts WHERE tenant_id='{PostgresFixture.TenantId}' AND quote_request_id='{rfq}'");

    private Task<long> AuditsAsync(Guid rfq) => db.ScalarAsync<long>(
        $"SELECT count(*) FROM audit_events WHERE tenant_id='{PostgresFixture.TenantId}' AND entity_id='{rfq}' AND action='{RfqExtractionExecutionRepository.Action}'");

    private static string Canonical(string rfq) => JsonSerializer.Serialize(new CanonicalRfqV1(
        new([rfq], rfq, [new("rfq_file", "drawing:v1")], 0.99m, RfqFactClassification.EXPLICIT, false, []),
        Missing<string>(), [], [], [], Missing<DateOnly?>(), Missing<DateOnly?>(), [], [], [], [], []));

    private static CanonicalRfqFact<T> Missing<T>() =>
        new([], default, [], null, RfqFactClassification.MISSING, false, []);
}
