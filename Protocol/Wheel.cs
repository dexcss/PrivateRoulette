using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using Newtonsoft.Json;

namespace PrivateRoulette.Protocol;

/// <summary>Static wheel data: American double-zero wheel (38 pockets).</summary>
public static class Wheel
{
    /// <summary>"00" is encoded as 37 internally and displayed as "00".</summary>
    public const int DoubleZero = 37;

    public const int PocketCount = 38;

    /// <summary>American pocket order clockwise, starting at 0 (37 = 00).</summary>
    public static readonly int[] Order =
    [
        0, 28, 9, 26, 30, 11, 7, 20, 32, 17, 5, 22, 34, 15, 3, 24, 36, 13, 1,
        37, 27, 10, 25, 29, 12, 8, 19, 31, 18, 6, 21, 33, 16, 4, 23, 35, 14, 2,
    ];

    /// <summary>Red pocket set (numbers only).</summary>
    public static readonly HashSet<int> Red = new() { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

    private static bool IsZero(int number) => number == 0 || number == DoubleZero;

    public static string ColorOf(int number) => IsZero(number) ? "green" : Red.Contains(number) ? "red" : "black";

    public static string Display(int number) => number == DoubleZero ? "00" : number.ToString();

    /// <summary>Index of a pocket in wheel order.</summary>
    public static int PositionOf(int number)
    {
        for (var i = 0; i < Order.Length; i++)
            if (Order[i] == number)
                return i;
        return -1;
    }
}

/// <summary>Parsed bet target with payout semantics. Keys match server vocabulary exactly.</summary>
public readonly record struct BetTarget(string Key)
{
    public bool IsInside => Key.StartsWith("n-", StringComparison.Ordinal)
                            || Key.StartsWith("split-", StringComparison.Ordinal)
                            || Key.StartsWith("street-", StringComparison.Ordinal)
                            || Key.StartsWith("corner-", StringComparison.Ordinal)
                            || Key.StartsWith("sixline-", StringComparison.Ordinal)
                            || Key is "firstfour" or "topline";

    private static bool IsZero(int n) => n == 0 || n == Wheel.DoubleZero;

    /// <summary>Payout multiplier applied to the stake when this target wins (stake is returned net).</summary>
    public int MultiplierFor(int result)
    {
        var t = Key.Trim().ToLowerInvariant();

        if (t.StartsWith("n-", StringComparison.Ordinal))
            return int.TryParse(t.AsSpan(2), out var n) && result == n ? 36 : 0;

        switch (t)
        {
            case "red": return IsZero(result) ? 0 : Wheel.ColorOf(result) == "red" ? 2 : 0;
            case "black": return IsZero(result) ? 0 : Wheel.ColorOf(result) == "black" ? 2 : 0;
            case "odd": return IsZero(result) ? 0 : (result % 2 == 1 ? 2 : 0);
            case "even": return IsZero(result) ? 0 : (result % 2 == 0 ? 2 : 0);
            case "low": return !IsZero(result) && result <= 18 ? 2 : 0;
            case "high": return !IsZero(result) && result >= 19 ? 2 : 0;
            case "firstfour": return result is >= 0 and <= 3 ? 9 : 0;
            case "topline": return result == 0 || result == Wheel.DoubleZero || result is >= 1 and <= 3 ? 7 : 0;
        }

        if (t.StartsWith("dozen-", StringComparison.Ordinal))
        {
            if (!int.TryParse(t.AsSpan(6), out var d) || d is < 1 or > 3) return 0;
            var dozen = IsZero(result) ? 0 : (result - 1) / 12 + 1;
            return dozen == d ? 3 : 0;
        }

        if (t.StartsWith("col-", StringComparison.Ordinal))
        {
            if (!int.TryParse(t.AsSpan(4), out var c) || c is < 1 or > 3) return 0;
            if (IsZero(result)) return 0;
            var col = result % 3 == 0 ? 3 : result % 3;
            return col == c ? 3 : 0;
        }

        if (TryNumberList(t, "split-", 2, out var split))
            return split.Contains(result) ? 18 : 0;

        if (TryNumberList(t, "street-", 3, out var street))
            return street.Contains(result) ? 12 : 0;

        if (TryNumberList(t, "corner-", 4, out var corner))
            return corner.Contains(result) ? 9 : 0;

        if (TryNumberList(t, "sixline-", 6, out var six))
            return six.Contains(result) ? 6 : 0;

        return 0;
    }

    private static bool TryNumberList(string t, string prefix, int count, out List<int> numbers)
    {
        numbers = new List<int>();
        if (!t.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var parts = t[prefix.Length..].Split('-');
        if (parts.Length != count) return false;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n is < 0 or > Wheel.DoubleZero) return false;
            numbers.Add(n);
        }

        return true;
    }

    public override string ToString()
    {
        var t = Key;
        if (t.StartsWith("n-", StringComparison.Ordinal))
            return int.TryParse(t.AsSpan(2), out var n) ? $"Straight {Wheel.Display(n)}" : "Straight";
        if (t.StartsWith("split-", StringComparison.Ordinal)) return $"Split {t[6..].Replace("-", "/")}";
        if (t.StartsWith("street-", StringComparison.Ordinal)) return $"Street {t[7..].Replace("-", "/")}";
        if (t.StartsWith("corner-", StringComparison.Ordinal)) return $"Corner {t[7..].Replace("-", "/")}";
        if (t.StartsWith("sixline-", StringComparison.Ordinal)) return $"Six Line {t[8..].Replace("-", "/")}";
        if (t.StartsWith("dozen-", StringComparison.Ordinal)) return $"Dozen {t[6..]}";
        if (t.StartsWith("col-", StringComparison.Ordinal)) return $"Column {t[4..]}";
        if (t == "topline") return "Top line 0-00-1-2-3";
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}

/// <summary>Verifies the server's provable-fairness proof for a spin.</summary>
public static class Fairness
{
    /// <summary>
    /// Verified formula (American wheel): sha256("{dice_roll}:{control_number}"),
    /// first 8 hex chars as big-endian uint32 seed, result = seed % 38.
    /// </summary>
    public static (bool Valid, int Result, uint Seed, string Hash) Verify(long diceRoll, string controlNumber, int claimedResult)
    {
        var input = $"{diceRoll}:{controlNumber}";
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        var seed = Convert.ToUInt32(hash[..8], 16);
        var result = (int)(seed % Wheel.PocketCount);
        return (result == claimedResult, result, seed, hash[..16]);
    }
}
