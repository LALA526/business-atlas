using System.Text.Json;

namespace ImportEvidence.Contracts;

public sealed record UploadView(Guid UploadId, Guid? BlobId, string State, string? Sha256, long? ByteSize, long Version);
public sealed record ImportEntryView(Guid EntryId, string ExternalId, string Kind, string Status, string Summary, string? Decision, Guid? UnitId = null);
public sealed record ImportUnknown(string Code, string Message, string? ExternalId = null);
public sealed record ImportBatchView(Guid ImportId, string? PackageId, Guid BaseBaselineId, string State, long Version,
    IReadOnlyList<ImportEntryView> Entries, Guid? ChangeSetId, IReadOnlyList<ImportUnknown> Unknowns);
public sealed record ImportBatchPage(IReadOnlyList<ImportBatchView> Items, string? NextCursor);
public sealed record ImportDecisionInput(Guid EntryId, string Decision, string Reason, Guid? UnitId = null);
public sealed record ImportDecisionsRequest(IReadOnlyList<ImportDecisionInput> Decisions);
public sealed record StageImportRequest(Guid? ChangeSetId);
public sealed record DiscardImportRequest(string Reason);
public sealed record ChangeSetView(Guid ChangeSetId, Guid BaseBaselineId, string Title, string State, long Version);
public sealed record SourceContent(Stream Content, string FileName, string ContentType, long ByteSize, string Sha256) : IDisposable
{ public void Dispose() => Content.Dispose(); }

public sealed record NormalizedEntry(string ExternalType, string ExternalId, string Title, string Kind,
    string FactKind, string Verification, JsonElement Applicability, JsonElement Body, JsonElement CategoryKeys,
    JsonElement Original, IReadOnlyList<string> SourceIds);
public sealed record NormalizedPackage(string PackageId, string FormatVersion, string Namespace,
    IReadOnlyList<NormalizedEntry> Entries, IReadOnlyList<ImportUnknown> Unknowns);
