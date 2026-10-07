using System.Text.Json;

namespace Anitools.Core.Tests;

/// <summary>Эталоны на месте, читаются и сняты с текущей версии оригинала.</summary>
public sealed class GoldenFilesTests
{
    public static TheoryData<string> Names() => new(GoldenFile.AllNames());

    [Fact]
    public void Golden_directory_is_not_empty() =>
        Assert.True(GoldenFile.AllNames().Count >= 30, "Эталонов нет — запусти python3 tools/gen_golden.py");

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_file_is_valid_and_up_to_date(string name)
    {
        var golden = GoldenFile.Load(name);

        Assert.Equal(name, golden.Root.GetProperty("function").GetString());
        Assert.Equal(GoldenFile.ReferenceSource, golden.Root.GetProperty("source").GetString());
        Assert.Equal(GoldenFile.ReferenceSha256(), golden.Root.GetProperty("source_sha256").GetString());
        Assert.NotEmpty(golden.Cases);
        Assert.All(golden.Cases, c =>
        {
            Assert.True(c.Output.HasValue ^ c.Error is not null, $"У случая должен быть ровно один из output/error: {c.Raw}");
            if (c.Error is not null)
            {
                Assert.Matches("^[A-Z][A-Za-z]*(Error|Exception)$", c.Error);
            }
        });
    }

    [Fact]
    public void Reference_hash_matches_original_from_plan()
    {
        // docs/PLAN.md: sha256 оригинала на Windows (CRLF) — 8920902b…49d50a. В репозитории файл с LF,
        // хэш считается после приведения CRLF → LF, поэтому здесь — LF-версия того же файла.
        Assert.Equal("224c08b02bef17d32b6981cb15dc4711e4ec00036e20f0f31593e2b7d2c3da9a", GoldenFile.ReferenceSha256());
    }

    [Fact]
    public void Fixtures_are_objects_when_present()
    {
        foreach (var name in GoldenFile.AllNames())
        {
            var root = GoldenFile.Load(name).Root;
            if (root.TryGetProperty("fixtures", out var fixtures))
            {
                Assert.Equal(JsonValueKind.Object, fixtures.ValueKind);
            }
        }
    }
}
