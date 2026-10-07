namespace Anitools.Core.Parsing;

/// <summary>Арифметика с плавающей точкой «как в CPython» — чтобы расчёты битрейта совпадали до бита.</summary>
internal static partial class PyText
{
    /// <summary>
    /// sum() по числам с плавающей точкой: с Python 3.12 — компенсированное суммирование Ноймайера
    /// (builtin_sum_impl), а не простое накопление.
    /// </summary>
    public static double Sum(IEnumerable<double> values)
    {
        var sum = 0.0;
        var c = 0.0;
        foreach (var x in values)
        {
            var t = sum + x;
            c += Math.Abs(sum) >= Math.Abs(x) ? (sum - t) + x : (x - t) + sum;
            sum = t;
        }

        // Не теряем знак у отрицательного результата и не превращаем бесконечность в NaN
        return c != 0 && double.IsFinite(c) ? sum + c : sum;
    }

    /// <summary>a // b для float (float_floor_div): через fmod, с «прилипанием» частного к ближайшему целому.</summary>
    public static double FloorDiv(double a, double b)
    {
        var mod = a % b; // в .NET % для double — это fmod
        var div = (a - mod) / b;
        if (mod != 0 && (b < 0) != (mod < 0))
        {
            div -= 1.0;
        }

        if (div == 0)
        {
            return Math.CopySign(0.0, a / b);
        }

        var floor = Math.Floor(div);
        return div - floor > 0.5 ? floor + 1.0 : floor;
    }
}
