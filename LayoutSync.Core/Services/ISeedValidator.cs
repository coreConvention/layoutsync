using System.Text.Json.Nodes;
using LayoutSync.Models;

namespace LayoutSync.Services;

/// <summary>
/// Batch-lifecycle contract for the detection-only seed/document validators driven by
/// <see cref="DocumentSyncService"/>. The service injects the full set
/// (<see cref="IEnumerable{T}"/> of <see cref="ISeedValidator"/>) and drives every validator
/// through the same <c>Reset</c> → <c>Inspect</c> (per file) → <c>FinalizeBatch</c> lifecycle, so
/// adding a validator is one new class + one DI line with no edits to the sync pipeline or the
/// <c>--strict</c> gate.
///
/// The lifecycle deliberately captures more than a single <c>Inspect</c> method because the
/// cross-reference validator is stateful and multi-phase: it accumulates per-file metadata during
/// the batch and only performs its cross-check once every file has been seen
/// (<see cref="FinalizeBatch"/>). Validators that need no batch-end phase inherit the default no-op.
/// </summary>
public interface ISeedValidator
{
    /// <summary>Short human name used in <c>--strict</c> diagnostics (e.g. "authorship").</summary>
    string Name { get; }

    /// <summary>
    /// Number of files that emitted at least one warning during the current batch. One offense per
    /// file (not per field/prop), matching the operator-facing WARN-per-file log shape. Consumed by
    /// <c>--strict</c> mode to fail the run with exit code 2 when any warnings are present.
    /// </summary>
    int WarningCount { get; }

    /// <summary>
    /// Remediation guidance appended after the count in the <c>--strict</c> error when
    /// <see cref="WarningCount"/> &gt; 0 (e.g. "seed file(s) with raw-NanoID identity references.
    /// Migrate …"). Lets the generic strict-mode loop emit each validator's tailored message.
    /// </summary>
    string StrictWarningDetail { get; }

    /// <summary>
    /// Called once at the start of every sync batch; clears accumulated batch state. In watch mode
    /// (repeated batches in one process) this is what stops counters/accumulators from leaking
    /// across batches.
    /// </summary>
    void Reset();

    /// <summary>
    /// Called once per file during the batch. Pure — <paramref name="content"/> is never mutated.
    /// </summary>
    /// <param name="documentType">Document type as classified by LayoutSync.</param>
    /// <param name="relativePath">Relative path used in warning messages (and as a record key).</param>
    /// <param name="content">Wrapped document content (identifier/type/indexes/data/@metadata).</param>
    void Inspect(DocumentType documentType, string relativePath, JsonObject content);

    /// <summary>
    /// Called once after every file in the batch has been inspected. Stateful, multi-phase
    /// validators run their cross-check here and emit any deferred warnings; returns the number of
    /// warnings emitted in this phase. Defaults to a no-op for single-phase validators.
    /// </summary>
    int FinalizeBatch() => 0;
}
