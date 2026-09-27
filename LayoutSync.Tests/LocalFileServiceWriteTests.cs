using System.Text.Json;
using System.Text.Json.Nodes;
using LayoutSync.Models;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Issue #38: a write must not change what the caller did not change. With the default
/// System.Text.Json encoder, every write escaped apostrophes, HTML-sensitive and non-ASCII
/// characters and dropped the trailing newline, rewriting lines nobody touched.
/// </summary>
public class LocalFileServiceWriteTests : IDisposable
{
    /// <summary>
    /// One of each kind of character the default encoder escaped: an em dash (non-ASCII), an
    /// apostrophe and <c>&lt; &gt; &amp; +</c> (HTML-sensitive), and an emoji, which is outside the
    /// Basic Multilingual Plane and so is escaped even by <c>UnsafeRelaxedJsonEscaping</c>.
    /// </summary>
    private static readonly string Description =
        "Demo — the BFF's <config> & a+b " + char.ConvertFromUtf32(0x1F680);

    private readonly string _tempRoot;
    private readonly string _layoutsPath;
    private readonly string _manifestPath;
    private readonly LocalFileService _fileService = new(NullLogger<LocalFileService>.Instance);

    public LocalFileServiceWriteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"write-tests-{Guid.NewGuid()}");
        _layoutsPath = Path.Combine(_tempRoot, "layouts");
        string manifestDir = Path.Combine(_layoutsPath, "demo", "manifests");
        Directory.CreateDirectory(manifestDir);
        _manifestPath = Path.Combine(manifestDir, "layout-manifest.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WriteDocumentAsync_UnchangedManifest_IsRewrittenByteForByte()
    {
        // Read from disk first, as every caller does: parsed strings are unescaped on read
        // and escaped again on write, so they are not protected by having come from the file.
        string manifest = Manifest();
        File.WriteAllText(_manifestPath, manifest);

        await ReadAndRewriteAsync();

        string written = File.ReadAllText(_manifestPath);
        Assert.Contains("\"description\": \"" + Description + "\"", written, StringComparison.Ordinal);
        Assert.EndsWith("}" + Environment.NewLine, written, StringComparison.Ordinal);
        Assert.Equal(manifest, written);
    }

    [Fact]
    public async Task WriteDocumentAsync_FileFromThePreviousWriter_IsNormalizedOnce()
    {
        // What WriteDocumentAsync used to write: default encoder, no final newline.
        string legacy = JsonNode.Parse(Manifest())!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("BFF\\u0027s", legacy, StringComparison.Ordinal);
        Assert.EndsWith("}", legacy, StringComparison.Ordinal);
        File.WriteAllText(_manifestPath, legacy);

        await ReadAndRewriteAsync();
        string first = File.ReadAllText(_manifestPath);
        await ReadAndRewriteAsync();

        Assert.Equal(Manifest(), first);
        Assert.Equal(first, File.ReadAllText(_manifestPath));
    }

    [Fact]
    public async Task WriteDocumentAsync_StillEscapesWhatJsonRequires()
    {
        // Built in code rather than parsed from a file, so the writer takes its other
        // (UTF-16) path through the encoder.
        JsonObject content = new()
        {
            ["quoted"] = "say \"hi\"",
            ["path"] = @"C:\layouts",
            ["controls"] = "tab\there, newline\nhere, nul\0here",
            ["description"] = Description,
        };

        await _fileService.WriteDocumentAsync(_manifestPath, content);

        string written = File.ReadAllText(_manifestPath);
        Assert.True(JsonNode.DeepEquals(content, JsonNode.Parse(written)));
        Assert.Contains(Description, written, StringComparison.Ordinal);
        // Ordinal: a culture-aware search treats NUL as ignorable and "finds" it anywhere.
        Assert.DoesNotContain("\t", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteDocumentAsync_LoneSurrogate_IsWrittenAsTheReplacementCharacter()
    {
        // Half of a surrogate pair cannot be written as UTF-8. The encoder reports it, and the
        // write substitutes U+FFFD rather than throwing or producing an invalid file.
        char highSurrogate = (char)0xD83D;
        char replacement = (char)0xFFFD;
        JsonObject content = new() { ["v"] = "a" + highSurrogate + "b" };

        await _fileService.WriteDocumentAsync(_manifestPath, content);

        Assert.Equal("a" + replacement + "b", JsonNode.Parse(File.ReadAllText(_manifestPath))?["v"]?.GetValue<string>());
    }

    private async Task ReadAndRewriteAsync()
    {
        SyncDocument? doc = await _fileService.ReadDocumentAsync(_manifestPath, _layoutsPath);
        Assert.NotNull(doc?.Content);
        await _fileService.WriteDocumentAsync(_manifestPath, doc.Content);
    }

    /// <summary>
    /// A manifest exactly as the writer formats it: indented, characters literal, the
    /// writer's line endings (<c>JsonSerializerOptions.NewLine</c> defaults to
    /// <see cref="Environment.NewLine"/>) and one final newline.
    /// </summary>
    private static string Manifest() => $$"""
        {
          "identifier": "demo",
          "description": "{{Description}}",
          "entities": {
            "sections": [
              {
                "identifier": "full-width-layout",
                "type": "ui-schema-section"
              }
            ]
          }
        }
        """.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
}
