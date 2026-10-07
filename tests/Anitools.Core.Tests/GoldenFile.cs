using System.Security.Cryptography;
using System.Text.Json;
using Anitools.Tests.Shared;

namespace Anitools.Core.Tests;

/// <summary>
/// Эталон из Golden/*.json — ответы оригинала reference/python/anitools.py,
/// снятые tools/gen_golden.py. Формат описан в Golden/README.md.
/// </summary>
internal sealed class GoldenFile
{
    public const string ReferenceSource = "reference/python/anitools.py";

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

    /// <summary>SHA-256 оригинала с переводами строк LF — так же, как считает gen_golden.py.</summary>
    public static string ReferenceSha256()
    {
        var bytes = File.ReadAllBytes(RepoRoot.Combine(ReferenceSource.Split('/')));
        var lf = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                continue;
            }

            lf.Add(bytes[i]);
        }

        return Convert.ToHexStringLower(SHA256.HashData(lf.ToArray()));
    }
}

/// <summary>Один случай: вход и либо ответ, либо имя исключения Python (ValueError…).</summary>
internal sealed record GoldenCase(JsonElement Input, JsonElement? Output, string? Error, JsonElement Raw);
