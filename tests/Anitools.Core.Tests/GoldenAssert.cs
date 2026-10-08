using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Anitools.Core.Tests;

/// <summary>Сверка реализации с golden-эталоном: все случаи разом, с полным списком расхождений.</summary>
internal static class GoldenAssert
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Прогоняет каждый случай эталона через <paramref name="run"/> и сверяет ответ с output
    /// (или исключение — с error). <paramref name="knownDifferences"/> — осознанные расхождения:
    /// вход (сырой JSON) → причина; такой случай обязан расходиться, иначе его надо убрать из списка.
    /// </summary>
    public static void All(
        string name,
        Func<JsonElement, object?> run,
        IReadOnlyDictionary<string, string>? knownDifferences = null,
        double relativeTolerance = 0)
    {
        var golden = GoldenFile.Load(name);
        var known = knownDifferences ?? new Dictionary<string, string>();
        var failures = new List<string>();
        foreach (var c in golden.Cases)
        {
            var key = c.Input.GetRawText();
            var expected = c.Error is not null ? $"исключение {c.Error}" : c.Output!.Value.GetRawText();
            string actual;
            bool ok;
            try
            {
                var element = JsonSerializer.SerializeToElement(Normalize(run(c.Input)), Json);
                actual = element.GetRawText();
                ok = c.Output is { } output && JsonEquals(output, element, relativeTolerance);
            }
            catch (Exception ex) when (ErrorName(ex) is { } error)
            {
                actual = $"исключение {error} ({ex.GetType().Name}: {ex.Message})";
                ok = c.Error == error;
            }

            if (known.ContainsKey(key))
            {
                if (ok)
                {
                    failures.Add($"{key}: помечен как расхождение, но совпал с эталоном — убрать из списка");
                }

                continue;
            }

            if (!ok)
            {
                failures.Add($"{key}\n      ждали: {expected}\n      вышло: {actual}");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{name}: {failures.Count} расхождений из {golden.Cases.Count}:\n  " + string.Join("\n  ", failures.Take(50)));
    }

    /// <summary>Имя ошибки в эталоне (ValueError…), которому соответствует исключение .NET, или null (тогда тест падает как есть).</summary>
    public static string? ErrorName(Exception ex) => ex switch
    {
        FormatException or OverflowException or ArgumentOutOfRangeException => "ValueError",
        KeyNotFoundException => "KeyError",
        IndexOutOfRangeException => "IndexError",
        _ => null,
    };

    /// <summary>Кортежи → массивы: в JSON эталона кортеж — это список.</summary>
    private static object? Normalize(object? value) => value switch
    {
        null or string => value,
        ITuple tuple => Enumerable.Range(0, tuple.Length).Select(i => Normalize(tuple[i])).ToArray(),
        IDictionary dict => dict.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture)!, k => Normalize(dict[k])),
        IEnumerable items => items.Cast<object?>().Select(Normalize).ToArray(),
        _ => value,
    };

    public static bool JsonEquals(JsonElement expected, JsonElement actual, double relativeTolerance = 0)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var e = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var a = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return e.Count == a.Count
                    && e.All(p => a.TryGetValue(p.Key, out var v) && JsonEquals(p.Value, v, relativeTolerance));
            case JsonValueKind.Array:
                return expected.GetArrayLength() == actual.GetArrayLength()
                    && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(p => JsonEquals(p.First, p.Second, relativeTolerance));
            case JsonValueKind.String:
                return expected.GetString() == actual.GetString();
            case JsonValueKind.Number:
                if (expected.TryGetInt64(out var ei) && actual.TryGetInt64(out var ai))
                {
                    return ei == ai;
                }

                var ed = expected.GetDouble();
                var ad = actual.GetDouble();
                return ed == ad || Math.Abs(ed - ad) <= relativeTolerance * Math.Max(Math.Abs(ed), Math.Abs(ad));
            default:
                return true;
        }
    }
}
