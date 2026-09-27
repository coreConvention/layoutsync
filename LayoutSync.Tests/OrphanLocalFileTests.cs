using LayoutSync.Models;
using LayoutSync.Services;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Unit tests for <see cref="DocumentSyncService.CountsAsLocalFile"/> — which sync results mark a
/// stored document as "has a local file" for orphan detection.
///
/// An orphan is a stored document with no local file. Tracking used to require a SUCCESSFUL write,
/// so any failed write made its still-present document look orphaned and <c>--clean</c> deleted
/// it. That matters more now that a replace is refused, by design, when the document changed after
/// it was compared (issue #24): the edit the refusal protects must not be deleted instead.
/// </summary>
public class OrphanLocalFileTests
{
    private static SyncDocument ReadFile(DocumentType type, string identifier = "home-hero") => new()
    {
        Identifier = identifier,
        DocumentType = type,
        FilePath = "layouts/dirt-life/sections/home-hero.json",
    };

    [Fact]
    public void FailedReplace_StillCountsAsLocalFile()
        => Assert.True(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Failed(ReadFile(DocumentType.Section), SyncAction.Replaced, "changed after it was compared")));

    [Fact]
    public void FailedCreate_StillCountsAsLocalFile()
        => Assert.True(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Failed(ReadFile(DocumentType.Manifest), SyncAction.Created, "write failed")));

    [Fact]
    public void SuccessfulAndUnchangedResults_CountAsLocalFile()
    {
        Assert.True(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Succeeded(ReadFile(DocumentType.Section), SyncAction.Replaced)));
        Assert.True(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Unchanged(ReadFile(DocumentType.Section), "Abc123_-Def456Ghi789x")));
    }

    [Fact]
    public void UnreadableFile_DoesNotCount()
        // SyncFileAsync reports an unreadable file with a bare document: no identifier to track.
        => Assert.False(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Failed(new SyncDocument { FilePath = "layouts/x/sections/broken.json" }, SyncAction.Skipped, "Failed to read file")));

    [Theory]
    [InlineData(DocumentType.Entity)]
    [InlineData(DocumentType.Identity)]
    public void UserData_IsNeverTracked(DocumentType type)
        // Entities and identities are never orphan-cleaned (issue #282), so they are never tracked.
        => Assert.False(DocumentSyncService.CountsAsLocalFile(
            SyncResult.Succeeded(ReadFile(type), SyncAction.Created)));
}
