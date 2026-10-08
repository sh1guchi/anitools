using System.Text.Json;
using Anitools.Core.Operations.Subtitles;

namespace Anitools.Core.Tests;

/// <summary>Эталоны на месте и читаются.</summary>
public sealed class GoldenFilesTests
{
    public static TheoryData<string> Names() => new(GoldenFile.AllNames());

    [Fact]
    public void Golden_directory_is_not_empty() =>
        Assert.True(GoldenFile.AllNames().Count >= 30, "Эталонов нет в tests/Anitools.Core.Tests/Golden");

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_file_is_valid(string name)
    {
        var golden = GoldenFile.Load(name);

        Assert.Equal(name, golden.Root.GetProperty("function").GetString());
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
    public void GoldenAssert_reports_wrong_answers_and_missing_errors()
    {
        // неверный ответ
        Assert.ThrowsAny<Exception>(() => GoldenAssert.All("codec_id_to_ext", _ => ".ass"));
        // ответ вместо исключения (в parse_track_ids есть случаи с ValueError)
        Assert.ThrowsAny<Exception>(() => GoldenAssert.All("parse_track_ids", _ => new[] { 0 }));
        // верная реализация проходит, но лишняя пометка «известное расхождение» на совпадающем случае — ошибка
        GoldenAssert.All("codec_id_to_ext", input => SubtitleTrackMatcher.CodecIdToExtension(input.GetString()!));
        Assert.ThrowsAny<Exception>(() => GoldenAssert.All(
            "codec_id_to_ext",
            input => SubtitleTrackMatcher.CodecIdToExtension(input.GetString()!),
            new Dictionary<string, string> { ["\"S_TEXT/ASS\""] = "совпадает — пометка лишняя" }));
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
