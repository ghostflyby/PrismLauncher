// SPDX-License-Identifier: GPL-3.0-only
// The per-source migration decision records (STATE S3): the old "<source>_nomigrate.txt"
// empty marker becomes one auditable JSON file per source,
// <state-root>/migration/<source>.json = { source, decision: imported|declined, at, mode,
// instances, bytes } — the one-shot decision per legacy source survives as a record the API
// can show ("可审计、API 可展示上次迁移结果"). Legacy markers found on disk are upgraded in
// place on first sight (read → declined@unknown record → marker deleted), see
// <see cref="DataMigration.Decide"/>.

using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Migration;

/// <summary>One migration decision for one source (S3 schema; the record IS the file).</summary>
public sealed record MigrationDecisionRecord
{
    /// <summary>decision value: the import ran to completion.</summary>
    public const string Imported = "imported";

    /// <summary>decision value: the user declined the import.</summary>
    public const string Declined = "declined";

    /// <summary>The `at` value of records upgraded from a legacy marker: the empty marker
    /// carried no timestamp, so the moment of the original decision is unknown.</summary>
    public const string UnknownAt = "unknown";

    /// <summary>The source key (profile/scheme id: prism/polymc/multimc/flint-legacy) — also
    /// the file name stem.</summary>
    public required string Source { get; init; }

    /// <summary><see cref="Imported"/> or <see cref="Declined"/>.</summary>
    public required string Decision { get; init; }

    /// <summary>ISO-8601 instant of the decision, or <see cref="UnknownAt"/> for upgraded
    /// legacy markers.</summary>
    public required string At { get; init; }

    /// <summary>The import mode of an `imported` decision: "cow" | "move" | "copy"; empty
    /// for `declined` (nothing was imported).</summary>
    public string Mode { get; init; } = "";

    /// <summary>Instance count observed for the import (planned basis — the exact `bytes`
    /// 口径 stays open per STATE §4).</summary>
    public long Instances { get; init; }

    /// <summary>Byte total observed for the import (planned basis).</summary>
    public long Bytes { get; init; }
}

/// <summary>Reads and writes the per-source decision records under the state root's
/// migration/ directory (S3). One TypedJsonFile per record file; writes are atomic.</summary>
public sealed class MigrationDecisionStore
{
    private readonly string _migrationDir;
    private readonly JsonTypeInfo<MigrationDecisionRecord> _typeInfo;

    public MigrationDecisionStore(string migrationDir, JsonTypeInfo<MigrationDecisionRecord> typeInfo)
    {
        _migrationDir = migrationDir;
        _typeInfo = typeInfo;
    }

    /// <summary>The directory the per-source record files live in
    /// (&lt;state-root&gt;/migration).</summary>
    public string MigrationDir => _migrationDir;

    /// <summary>The record file for one source (&lt;dir&gt;/&lt;source&gt;.json).</summary>
    public string RecordPath(string source) =>
        Path.Combine(_migrationDir, SourceFileStem(source) + ".json");

    /// <summary>The stored decision for <paramref name="source"/>, or null when the source is
    /// undecided (a corrupt record counts as undecided — TypedJsonFile keeps the
    /// <c>.corrupt</c> evidence beside it).</summary>
    public MigrationDecisionRecord? Read(string source)
    {
        var path = RecordPath(source);
        if (!File.Exists(path))
        {
            return null;
        }

        // load-or-default with a null default: exists ⇒ parse, corrupt ⇒ undecided
        return new TypedJsonFile<MigrationDecisionRecord>(path, _typeInfo).LoadOrDefault(() => null!);
    }

    /// <summary>Write (or replace) the decision record for its source. The source name must
    /// be a bare file-name stem — the record file lives inside the migration directory.</summary>
    public void Write(MigrationDecisionRecord record)
    {
        var stem = SourceFileStem(record.Source);
        new TypedJsonFile<MigrationDecisionRecord>(
            Path.Combine(_migrationDir, stem + ".json"), _typeInfo).Save(record);
    }

    /// <summary>Guard the record file name: sources are fixed vocabulary ids (prism,
    /// polymc, multimc, flint-legacy, …) — anything path-like is refused rather than
    /// escaped into a surprising location.</summary>
    private static string SourceFileStem(string source)
    {
        if (string.IsNullOrEmpty(source) || source.Contains('/') || source.Contains('\\') ||
            source.Contains(Path.DirectorySeparatorChar) || source == "." || source == ".." ||
            source.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{source}' is not a valid migration source id", nameof(source));
        }

        return source;
    }
}
