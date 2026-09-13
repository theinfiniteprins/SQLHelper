using Microsoft.Data.SqlClient;
using SqlHelper.Core.Auditing;
using SqlHelper.Core.Backup;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Model;
using SqlHelper.Core.Patching;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Orchestration;

/// <summary>
/// The façade the UI talks to: every screen's "fire it" action is one call here. Wires together
/// the registry, the fan-out runner, the object inspector, the backup writer, the patch planner
/// and the audit log so no caller has to assemble that pipeline by hand.
/// </summary>
public sealed class DeploymentEngine
{
    private readonly ITargetConnectionFactory _connections;
    private readonly SqlExecutor _executor;
    private readonly ObjectInspector _inspector = new();
    private readonly AuditLog _audit;
    private readonly FanOutRunner _fanOut;

    public DeploymentEngine(ITargetConnectionFactory connections, AuditLog audit, FanOutOptions? fanOutOptions = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _executor = new SqlExecutor(connections);
        _fanOut = new FanOutRunner(fanOutOptions);
    }

    // ------------------------------------------------------------------
    // SELECT screen
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<TargetRun<QueryResult>>> RunSelectAsync(
        IReadOnlyList<DatabaseTarget> targets,
        string sql,
        QueryExecutionOptions? options = null,
        IProgress<TargetRun<QueryResult>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyVerdict verdict = ReadOnlyGuard.Inspect(sql);
        if (!verdict.IsReadOnly)
        {
            throw new InvalidOperationException(verdict.Summary);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<TargetRun<QueryResult>> runs = await _fanOut.RunAsync(
            targets, (t, ct) => _executor.ExecuteQueryAsync(t, sql, options, ct), progress, cancellationToken).ConfigureAwait(false);

        await AuditAsync("select", null, null, sql, runs.Select(r => new AuditTargetResult(
            r.Target.ClientName, r.Target.ServerName, r.Target.DatabaseName, r.Target.Environment.ToString(),
            Status(r.Status), r.Result?.TotalRows, r.Error, null, null)),
            stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        return runs;
    }

    // ------------------------------------------------------------------
    // Update / Delete screen
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<TargetRun<NonQueryResult>>> RunNonQueryAsync(
        IReadOnlyList<DatabaseTarget> targets,
        string script,
        NonQueryExecutionOptions options,
        string changeTitle,
        string? ticket = null,
        IProgress<TargetRun<NonQueryResult>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<TargetRun<NonQueryResult>> runs = await _fanOut.RunAsync(
            targets, (t, ct) => _executor.ExecuteNonQueryAsync(t, script, options, ct), progress, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            options.DryRun ? "nonquery-dry-run" : "nonquery", changeTitle, ticket, script,
            runs.Select(r => new AuditTargetResult(
                r.Target.ClientName, r.Target.ServerName, r.Target.DatabaseName, r.Target.Environment.ToString(),
                Status(r.Status), r.Result?.TotalRowsAffected, r.Error, null, null)),
            stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        return runs;
    }

    // ------------------------------------------------------------------
    // Programmable-object deployment — full replace
    // ------------------------------------------------------------------

    /// <summary>
    /// Deploys one new definition to every target: backs up whatever each target currently has
    /// (or records that the object is new), applies the change inside a transaction, re-reads the
    /// result to verify it landed, and writes a per-client rollback script plus a manifest.
    /// </summary>
    public async Task<DeployResult> DeployObjectAsync(
        IReadOnlyList<DatabaseTarget> targets,
        ObjectName objectName,
        string newDefinitionScript,
        string backupRoot,
        string changeTitle,
        string? ticket = null,
        IProgress<TargetRun<DeployTargetResult>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(newDefinitionScript);
        if (!extraction.Ok)
        {
            throw new InvalidOperationException(extraction.Error);
        }

        var writer = new BackupWriter(backupRoot);
        BackupSession session = writer.BeginSession(changeTitle, DateTimeOffset.Now);
        var manifest = NewManifest(changeTitle, ticket);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<TargetRun<DeployTargetResult>> runs = await _fanOut.RunAsync(
            targets,
            (t, ct) => DeployOneAsync(t, objectName, extraction, session, ct),
            progress,
            cancellationToken).ConfigureAwait(false);

        foreach (TargetRun<DeployTargetResult> run in runs)
        {
            manifest.Entries.Add(ToManifestEntry(run, objectName.Plain, extraction.Kind!.Value.ToString()));
        }

        await WriteRollbackScriptsAsync(session, runs, objectName, extraction.Kind!.Value, cancellationToken).ConfigureAwait(false);

        manifest.FinishedUtc = DateTimeOffset.UtcNow;
        string manifestPath = await session.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            "deploy-object", changeTitle, ticket, extraction.ModuleText,
            runs.Select(r => new AuditTargetResult(
                r.Target.ClientName, r.Target.ServerName, r.Target.DatabaseName, r.Target.Environment.ToString(),
                Status(r.Status), null, r.Error, r.Result?.BeforeHash, r.Result?.AfterHash)),
            stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        return new DeployResult(session.Folder, manifestPath, [.. runs.Select(r => r.Result ?? Failed(r))]);
    }

    // ------------------------------------------------------------------
    // Programmable-object deployment — targeted patch
    // ------------------------------------------------------------------

    /// <summary>Captures the current copy of an object on every target, for cohort analysis before authoring a patch.</summary>
    public async Task<IReadOnlyList<ObjectCapture>> CaptureForCohortAnalysisAsync(
        IReadOnlyList<DatabaseTarget> targets,
        ObjectName objectName,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TargetRun<ProgrammableObject?>> runs = await _fanOut.RunAsync(
            targets,
            async (t, ct) =>
            {
                await using SqlConnection connection = await _connections.OpenAsync(t, ct).ConfigureAwait(false);
                return await _inspector.GetAsync(connection, objectName, ct).ConfigureAwait(false);
            },
            null,
            cancellationToken).ConfigureAwait(false);

        return [.. runs.Select(r => r.IsSuccess
            ? new ObjectCapture(r.Target, r.Result)
            : new ObjectCapture(r.Target, null, r.Error ?? "Could not read this database."))];
    }

    /// <summary>Runs the patch planner for every target against its live current definition.</summary>
    public async Task<IReadOnlyList<PatchAttempt>> PlanPatchAsync(
        IReadOnlyList<DatabaseTarget> targets,
        ObjectName objectName,
        PatchDefinition patch,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TargetRun<PatchAttempt>> runs = await _fanOut.RunAsync(
            targets,
            async (t, ct) =>
            {
                await using SqlConnection connection = await _connections.OpenAsync(t, ct).ConfigureAwait(false);
                ProgrammableObject? current = await _inspector.GetAsync(connection, objectName, ct).ConfigureAwait(false);
                if (current is null)
                {
                    return new PatchAttempt(t, PatchOutcome.ManualRequired, string.Empty, null, [], null,
                        "Object does not exist on this database.");
                }

                if (!current.HasDefinition)
                {
                    return new PatchAttempt(t, PatchOutcome.ManualRequired, string.Empty, null, [], null,
                        "Object is encrypted — cannot read or patch its definition.");
                }

                return PatchPlanner.Plan(t, current, patch);
            },
            null,
            cancellationToken).ConfigureAwait(false);

        return [.. runs.Select(r => r.Result ?? new PatchAttempt(r.Target, PatchOutcome.ManualRequired, string.Empty, null, [], null, r.Error ?? "Failed to plan."))];
    }

    /// <summary>
    /// Applies a set of already-approved patch attempts (only ever ones with a non-null
    /// <see cref="PatchAttempt.PatchedBody"/>): backs up the current definition, applies the
    /// patched text, verifies, and writes rollback scripts and a manifest — same safety pipeline
    /// as a full replace.
    /// </summary>
    public async Task<DeployResult> ApplyPatchAsync(
        IReadOnlyList<PatchAttempt> approved,
        ObjectName objectName,
        string backupRoot,
        string changeTitle,
        string? ticket = null,
        IProgress<TargetRun<DeployTargetResult>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var toApply = approved.Where(a => a.PatchedBody is not null).ToList();
        if (toApply.Count == 0)
        {
            throw new InvalidOperationException("No approved patches to apply.");
        }

        var writer = new BackupWriter(backupRoot);
        BackupSession session = writer.BeginSession(changeTitle, DateTimeOffset.Now);
        var manifest = NewManifest(changeTitle, ticket);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<TargetRun<DeployTargetResult>> runs = await _fanOut.RunAsync(
            toApply.Select(a => a.Target).ToList(),
            (t, ct) =>
            {
                PatchAttempt attempt = toApply.First(a => a.Target.Id == t.Id);
                return ApplyOnePatchAsync(t, objectName, attempt, session, ct);
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        foreach (TargetRun<DeployTargetResult> run in runs)
        {
            manifest.Entries.Add(ToManifestEntry(run, objectName.Plain, "Unknown"));
        }

        ProgrammableObjectKind patchKind = runs
            .Select(r => r.Result?.PreviousDefinition?.Kind)
            .FirstOrDefault(k => k is not null) ?? ProgrammableObjectKind.StoredProcedure;
        await WriteRollbackScriptsAsync(session, runs, objectName, patchKind, cancellationToken).ConfigureAwait(false);

        manifest.FinishedUtc = DateTimeOffset.UtcNow;
        string manifestPath = await session.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

        await AuditAsync(
            "patch-object", changeTitle, ticket, objectName.Plain,
            runs.Select(r => new AuditTargetResult(
                r.Target.ClientName, r.Target.ServerName, r.Target.DatabaseName, r.Target.Environment.ToString(),
                Status(r.Status), null, r.Error, r.Result?.BeforeHash, r.Result?.AfterHash)),
            stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        return new DeployResult(session.Folder, manifestPath, [.. runs.Select(r => r.Result ?? Failed(r))]);
    }

    // ------------------------------------------------------------------
    // internals
    // ------------------------------------------------------------------

    private async Task<DeployTargetResult> DeployOneAsync(
        DatabaseTarget target, ObjectName objectName, ModuleExtraction extraction, BackupSession session, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await _connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        ProgrammableObject? current = await _inspector.GetAsync(connection, objectName, cancellationToken).ConfigureAwait(false);

        string? backupFile;
        string? beforeHash = null;
        var signatureChanges = new List<string>();

        if (current is null)
        {
            backupFile = await session.CaptureMissingAsync(target, objectName, cancellationToken).ConfigureAwait(false);
        }
        else if (!current.HasDefinition)
        {
            return new DeployTargetResult(target, false, "replace", "Object is encrypted — refusing to overwrite blind.", null, null, null, [], current.Kind.ToString());
        }
        else
        {
            backupFile = await session.CaptureAsync(target, current, cancellationToken).ConfigureAwait(false);
            beforeHash = TSqlNormalizer.ExactKey(current.Definition);

            RoutineSignature? beforeSig = RoutineSignature.FromDefinition(current.Definition, current.Kind);
            RoutineSignature? afterSig = RoutineSignature.FromDefinition(extraction.ModuleText, extraction.Kind!.Value);
            if (beforeSig is not null && afterSig is not null)
            {
                signatureChanges = [.. beforeSig.CompareWith(afterSig).Differences];
            }
        }

        var newModule = new ProgrammableObject(
            objectName, extraction.Kind!.Value, extraction.ModuleText,
            current?.UsesAnsiNulls ?? true, current?.UsesQuotedIdentifier ?? true,
            0, DateTime.UtcNow, DateTime.UtcNow, IsEncrypted: false);

        await RunModuleScriptInTransactionAsync(
            connection, newModule, current is null ? ModuleVerb.Create : ModuleVerb.Alter, cancellationToken).ConfigureAwait(false);

        ProgrammableObject? after = await _inspector.GetAsync(connection, objectName, cancellationToken).ConfigureAwait(false);
        string? afterHash = after?.HasDefinition == true ? TSqlNormalizer.ExactKey(after.Definition) : null;

        return new DeployTargetResult(target, true, current is null ? "new-object" : "replace", null, backupFile, beforeHash, afterHash, signatureChanges, extraction.Kind!.Value.ToString())
        {
            PreviousDefinition = current,
            WasNew = current is null,
        };
    }

    private async Task<DeployTargetResult> ApplyOnePatchAsync(
        DatabaseTarget target, ObjectName objectName, PatchAttempt attempt, BackupSession session, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await _connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
        ProgrammableObject? current = await _inspector.GetAsync(connection, objectName, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.HasDefinition)
        {
            return new DeployTargetResult(target, false, "patch", "Object is missing or encrypted at deploy time — it may have changed since planning.", null, null, null, [], current?.Kind.ToString());
        }

        // The plan — and the diff the operator approved — was built against the definition read
        // during planning. If this client has changed since then, the approved patched body no
        // longer describes reality, so refuse rather than overwrite someone else's change.
        if (!string.Equals(current.Definition, attempt.OriginalBody, StringComparison.Ordinal))
        {
            return new DeployTargetResult(target, false, "patch",
                "This client's definition changed after the plan was built, so the reviewed patch no longer matches what is live. Re-run \"Check all clients\" and review it again.",
                null, null, null, [], current.Kind.ToString());
        }

        string? backupFile = await session.CaptureAsync(target, current, cancellationToken).ConfigureAwait(false);
        string beforeHash = TSqlNormalizer.ExactKey(current.Definition);

        var patchedModule = current with { Definition = attempt.PatchedBody! };
        await RunModuleScriptInTransactionAsync(connection, patchedModule, ModuleVerb.Alter, cancellationToken).ConfigureAwait(false);

        ProgrammableObject? after = await _inspector.GetAsync(connection, objectName, cancellationToken).ConfigureAwait(false);
        string? afterHash = after?.HasDefinition == true ? TSqlNormalizer.ExactKey(after.Definition) : null;

        string method = attempt.HunkAttempts.Any(h => h.Tier == PatchTier.FuzzyAnchor) ? "patch-fuzzy" : "patch-exact";
        return new DeployTargetResult(target, true, method, null, backupFile, beforeHash, afterHash,
            attempt.Validation?.SignatureChange?.Differences ?? [], current.Kind.ToString())
        {
            PreviousDefinition = current,
        };
    }

    /// <summary>Writes a per-client rollback script for every target that actually changed, from what was captured before the change.</summary>
    private static async Task WriteRollbackScriptsAsync(
        BackupSession session,
        IEnumerable<TargetRun<DeployTargetResult>> runs,
        ObjectName objectName,
        ProgrammableObjectKind kind,
        CancellationToken cancellationToken)
    {
        foreach (TargetRun<DeployTargetResult> run in runs)
        {
            if (run.Result is not { Succeeded: true } result)
            {
                continue;
            }

            IReadOnlyList<ProgrammableObject> previous = result is { WasNew: false, PreviousDefinition.HasDefinition: true }
                ? [result.PreviousDefinition!]
                : [];
            IReadOnlyList<DroppedObject> created = result.WasNew ? [new DroppedObject(objectName, kind)] : [];

            if (previous.Count == 0 && created.Count == 0)
            {
                continue; // nothing captured to roll back to (e.g. was already encrypted)
            }

            await session.WriteRollbackAsync(run.Target, previous, created, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a module definition inside a transaction and proves what landed before committing.
    ///
    /// <paramref name="verb"/> is <c>ALTER</c> for an object that exists and <c>CREATE</c> for one that
    /// does not — never <c>CREATE OR ALTER</c>, which SQL Server stores with two stray spaces after
    /// the verb (see <see cref="ModuleScript"/>).
    ///
    /// After the script runs, and while the transaction is still open, the definition is read back.
    /// The server stores the text with its verb rewritten to <c>CREATE</c> and every other character
    /// unchanged, so it must equal <see cref="ModuleScript.ExpectedStoredDefinition"/> exactly. If it
    /// does not, nothing is committed: the database is left precisely as it was.
    /// </summary>
    private static async Task RunModuleScriptInTransactionAsync(
        SqlConnection connection, ProgrammableObject module, ModuleVerb verb, CancellationToken cancellationToken)
    {
        // Without a rewritable verb this would fire a bare CREATE at an object that already
        // exists, which fails on the server with a much less helpful message. Catch it here.
        if (!ModuleScript.CanRewriteVerb(module.Definition))
        {
            throw new InvalidOperationException(
                "Could not find the CREATE / ALTER at the start of this definition, so it was not run. " +
                "Nothing was changed on this database.");
        }

        string script = ModuleScript.ToRunnableScript(module, verb);
        string expectedStored = ModuleScript.ExpectedStoredDefinition(module.Definition).Trim();
        IReadOnlyList<ScriptBatch> batches = GoBatchSplitter.Split(script);

        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (ScriptBatch batch in batches)
            {
                await using SqlCommand command = connection.CreateCommand();
                command.CommandText = batch.Text;
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await VerifyStoredDefinitionAsync(connection, transaction, module.Name, expectedStored, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task VerifyStoredDefinitionAsync(
        SqlConnection connection, SqlTransaction transaction, ObjectName name, string expected, CancellationToken cancellationToken)
    {
        await using SqlCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT OBJECT_ID(@name), OBJECT_DEFINITION(OBJECT_ID(@name));";
        read.Parameters.AddWithValue("@name", name.Bracketed);

        await using SqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            throw new InvalidOperationException(
                "The object could not be found after the script ran, so the change was rolled back. Nothing was changed on this database.");
        }

        if (reader.IsDBNull(1))
        {
            // WITH ENCRYPTION: the server will not show the text back, so existence is all that can be confirmed.
            return;
        }

        string stored = reader.GetString(1);
        if (!string.Equals(stored, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "After running, the server did not hold exactly the reviewed definition, so the change was rolled back. " +
                "Nothing was changed on this database.");
        }
    }

    private static DeploymentManifest NewManifest(string changeTitle, string? ticket) => new()
    {
        ToolVersion = AppInfo.Version,
        Operator = AppInfo.Operator,
        Machine = AppInfo.Machine,
        StartedUtc = DateTimeOffset.UtcNow,
        ChangeTitle = changeTitle,
        Ticket = ticket,
    };

    private static ManifestEntry ToManifestEntry(TargetRun<DeployTargetResult> run, string objectName, string fallbackKind)
    {
        DeployTargetResult? result = run.Result;
        return new ManifestEntry
        {
            Client = run.Target.ClientName,
            Server = run.Target.ServerName,
            Database = run.Target.DatabaseName,
            Environment = run.Target.Environment.ToString(),
            ObjectName = objectName,
            ObjectKind = result?.ObjectKind ?? fallbackKind,
            Method = result?.Method ?? "unknown",
            Result = Status(run.Status),
            BeforeHash = result?.BeforeHash,
            AfterHash = result?.AfterHash,
            BackupFile = result?.BackupFile,
            Error = result?.Error ?? run.Error,
            SignatureChanges = result?.SignatureChanges ?? [],
        };
    }

    private static DeployTargetResult Failed(TargetRun<DeployTargetResult> run) =>
        new(run.Target, false, "unknown", run.Error, null, null, null, []);

    private static string Status(TargetStatus status) => status switch
    {
        TargetStatus.Succeeded => "succeeded",
        TargetStatus.Failed => "failed",
        TargetStatus.Cancelled => "cancelled",
        _ => "skipped",
    };

    private async Task AuditAsync(
        string action, string? changeTitle, string? ticket, string? statement,
        IEnumerable<AuditTargetResult> targets, TimeSpan duration, CancellationToken cancellationToken)
    {
        await _audit.AppendAsync(new AuditEvent
        {
            Operator = AppInfo.Operator,
            Machine = AppInfo.Machine,
            Action = action,
            ChangeTitle = changeTitle,
            Ticket = ticket,
            Statement = SecretRedactor.Scrub(statement),
            Targets = [.. targets],
            Duration = duration,
        }, cancellationToken).ConfigureAwait(false);
    }
}
