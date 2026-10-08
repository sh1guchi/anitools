using System.Text.Json;
using Anitools.Tests.Shared;

namespace Anitools.Core.Tests;

/// <summary>Эталон из Golden/*.json — входы и ожидаемые ответы. Формат описан в Golden/README.md.</summary>
internal sealed class GoldenFile
{
    private GoldenFile(string name, JsonElement root, IReadOnlyList<GoldenCase> cases)
    {
        Name = name;
        Root = root;
        Cases = cases;
    }

    public string Name { get; }

    /// <summary>Весь документ: шапка, фикстуры ("fixtures") и случаи.</summary>
    public JsonElement Root { get; }

    public IReadOnlyList<GoldenCase> Cases { get; }

    public static string DirectoryPath => RepoRoot.Combine("tests", "Anitools.Core.Tests", "Golden");

    public static IReadOnlyList<string> AllNames() =>
        Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static GoldenFile Load(string name)
    {
        var text = File.ReadAllText(Path.Combine(DirectoryPath, name + ".json"));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        var cases = root.GetProperty("cases").EnumerateArray()
            .Select(c => new GoldenCase(
                c.GetProperty("input"),
                c.TryGetProperty("output", out var output) ? output : null,
                c.TryGetProperty("error", out var error) ? error.GetString() : null,
                c))
            .ToList();
        return new GoldenFile(name, root, cases);
    }
}

/// <summary>Один случай: вход и либо ответ, либо имя ошибки (ValueError — неверный ввод и т.п.).</summary>
internal sealed record GoldenCase(JsonElement Input, JsonElement? Output, string? Error, JsonElement Raw);
