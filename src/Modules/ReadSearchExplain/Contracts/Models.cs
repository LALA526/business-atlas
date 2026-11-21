using System.Text.Json;
using AccessApplicability.Contracts;
using KnowledgeCore.Contracts;

namespace ReadSearchExplain.Contracts;

public sealed record SearchHit(Guid UnitId, Guid RevisionId, string Title, string Kind, string? Snippet);
public sealed record SearchHitPage(IReadOnlyList<SearchHit> Items, string? NextCursor);
public sealed record Unknown(string Key, string Description, string Severity, string Reason, string? NextAction = null, IReadOnlyList<UnitRef>? References = null);
public sealed record ReadingSection(string Key, string Title, IReadOnlyList<JsonElement> Blocks, IReadOnlyList<ReadingSection>? Children = null, string? ExpandRef = null);
public sealed record ReadingView(UnitRef? Root, string Heading, string Purpose, IReadOnlyList<ReadingSection> Sections, ResolvedReadContext Context, IReadOnlyList<Unknown> Unknowns, string Completeness, string? NextCursor = null);
public sealed record ReadingPlanRequest(Guid RootUnitId, ReadContextRequest ReadContext, string? Profile, string? Cursor);
public sealed record VocabularyRequest(string Term, string? ContextKey, ReadContextRequest ReadContext);
public sealed record VocabularyResult(string Status, IReadOnlyList<RevisionView> Matches, IReadOnlyList<Unknown> Unknowns);
public sealed record ContentReviewRequest(RevisionInput Revision, ReadContextRequest ReadContext, bool UseModel);
public sealed record ContentReviewResult(IReadOnlyList<Check> Checks, IReadOnlyList<Unknown> Unknowns, bool ModelReviewed);
public sealed record Check(string Key, string Status, bool Critical, string Reason, IReadOnlyList<JsonElement> Evidence, string? NextAction = null);
public sealed record CoverageRequest(Guid RootUnitId, ReadContextRequest ReadContext);
public sealed record CoverageResult(IReadOnlyList<Check> Checks, IReadOnlyList<Unknown> Unknowns);
public sealed record CreateExportRequest(ReadContextRequest ReadContext, string Format, IReadOnlyList<Guid> RootUnitIds);
public sealed record ExportArtifact(Guid UploadId, string FileName, string ContentType, byte[] Content, string Sha256, IReadOnlyList<UnitRef> Included, ResolvedReadContext Context);
