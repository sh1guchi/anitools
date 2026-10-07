using System.Text.Json;
using Anitools.Core.Operations.Common;
using Anitools.Core.Operations.Rename;
using Anitools.Core.Shikimori;
using Anitools.Core.Tests.Fixtures;

namespace Anitools.Core.Tests.Operations;

public sealed class RenameTests
{
    [Fact]
    public void Renames_like_original() =>
        GoldenAssert.All("rename_scenarios", input =>
        {
            using var dir = new TempDir();
            var files = input.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToList();
            foreach (var f in files)
            {
                dir.File(f, "x");
            }

            var o = input.GetProperty("options");
            var listed = RenameOperation.ListFiles(dir.Path);
            var start = o.GetProperty("start").GetInt32();
            Dictionary<string, string>? manual = null;
            if (o.GetProperty("manual").ValueKind == JsonValueKind.Object)
            {
                // null в сценарии — Enter: номер, который предлагает ручной режим оригинала
                manual = o.GetProperty("manual").EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => p.Value.ValueKind == JsonValueKind.Null ? RenameOperation.ManualSuggestion(p.Name, start) ?? "" : p.Value.GetString()!);
            }

            var rows = RenameOperation.Plan(dir.Path, listed, new RenameOptions
            {
                BaseName = o.GetProperty("base").GetString()!,
                NumberingStart = start,
                Suffix = o.GetProperty("suffix").GetString(),
                ManualNumbers = manual,
            });
            var result = RenameOperation.Execute(dir.Path, rows, dir.Combine("_journal"));

            // Подсказка для Shikimori → тот же поисковый запрос, что у оригинала
            var paths = new List<string>();
            if (input.GetProperty("shikimori").ValueKind == JsonValueKind.String)
            {
                var query = ShikimoriQuery.FromTitle(RenameOperation.TitleHint(listed));
                paths.Add($"animes?search={ShikimoriClient.Quote(query.SearchText)}&limit=15&order=popularity");
            }

            return new Dictionary<string, object>
            {
                ["renames"] = result.Renamed.Select(r => new[] { r.Old, r.New }).ToList(),
                ["shikimori_paths"] = paths,
            };
        });

    [Fact]
    public void Same_new_name_for_two_files_is_a_conflict_for_both()
    {
        using var dir = new TempDir();
        dir.File("Show - 01 v1.mkv");
        dir.File("Show - 01 v2.mkv");
        var rows = RenameOperation.Plan(dir.Path, RenameOperation.ListFiles(dir.Path), new RenameOptions { BaseName = "Show" });
        Assert.All(rows, r => Assert.Equal(RenameRowStatus.Conflict, r.Status));
    }

    [Fact]
    public void Undo_restores_names_from_journal()
    {
        using var dir = new TempDir();
        dir.File("[Group] Show - 01 [1080p].mkv", "1");
        dir.File("[Group] Show - 02 [1080p].mkv", "2");
        var rows = RenameOperation.Plan(dir.Path, RenameOperation.ListFiles(dir.Path), new RenameOptions { BaseName = "Show: Season" });
        var result = RenameOperation.Execute(dir.Path, rows, dir.Combine("_logs"), new DateTime(2026, 10, 7, 21, 0, 0));

        Assert.Equal(["Show - Season - 01.mkv", "Show - Season - 02.mkv"], RenameOperation.ListFiles(dir.Path));
        Assert.Equal(result.JournalPath, RenameJournal.Latest(dir.Combine("_logs")));
        Assert.EndsWith("rename_2026-10-07_21-00-00.json", result.JournalPath);

        var undo = RenameJournal.Undo(result.JournalPath!);
        Assert.Empty(undo.Failed);
        Assert.Equal(["[Group] Show - 01 [1080p].mkv", "[Group] Show - 02 [1080p].mkv"], RenameOperation.ListFiles(dir.Path));
        Assert.Null(RenameJournal.Latest(dir.Combine("_logs")));
    }

    [Fact]
    public void Undo_leaves_taken_name_alone_and_keeps_it_in_journal()
    {
        using var dir = new TempDir();
        dir.File("[Group] Show - 01 [1080p].mkv", "1");
        dir.File("[Group] Show - 02 [1080p].mkv", "2");
        var rows = RenameOperation.Plan(dir.Path, RenameOperation.ListFiles(dir.Path), new RenameOptions { BaseName = "Show" });
        var journal = RenameOperation.Execute(dir.Path, rows, dir.Combine("_logs")).JournalPath!;
        dir.File("[Group] Show - 01 [1080p].mkv", "новый файл со старым именем");

        var undo = RenameJournal.Undo(journal);

        Assert.Equal([("Show - 02.mkv", "[Group] Show - 02 [1080p].mkv")], undo.Renamed);
        Assert.Equal([("Show - 01.mkv", "имя [Group] Show - 01 [1080p].mkv уже занято")], undo.Failed);
        Assert.Equal("новый файл со старым именем", File.ReadAllText(dir.Combine("[Group] Show - 01 [1080p].mkv")));

        // В журнале осталась только серия 01: освободили имя — повторный откат её возвращает
        Assert.Equal(journal, undo.JournalPath);
        File.Delete(dir.Combine("[Group] Show - 01 [1080p].mkv"));
        var again = RenameJournal.Undo(journal);
        Assert.Equal([("Show - 01.mkv", "[Group] Show - 01 [1080p].mkv")], again.Renamed);
        Assert.Empty(again.Failed);
        Assert.Null(again.JournalPath);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Bad_options_are_plan_errors()
    {
        using var dir = new TempDir();
        Assert.Throws<PlanException>(() => RenameOperation.Plan(dir.Path, [], new RenameOptions { BaseName = " .. " }));
        Assert.Throws<PlanException>(() => RenameOperation.Plan(dir.Path, [], new RenameOptions { BaseName = "X", NumberingStart = 0 }));
    }

    [Theory]
    [InlineData("13", 12, "02")]
    [InlineData("12", 12, "01")]
    [InlineData("11", 12, null)]
    [InlineData("1100", 1, "1100")]
    public void Adjust_numbering(string raw, int start, string? expected) => Assert.Equal(expected, RenameOperation.Adjust(raw, start));
}
