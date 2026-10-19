using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using Newtonsoft.Json;

namespace PrivateRoulette.Protocol;

/// <summary>Static wheel data: 37-pocket European layout.</summary>
public static class Wheel
{
    /// <summary>Pocket order clockwise, starting at 0. Verified against server artwork.</summary>
    public static readonly int[] Order =
    [
        0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
        5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
    ];

    /// <summary>Red pocket set.</summary>
    public static readonly HashSet<int> Red = new() { 1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36 };

    public const int PocketCount = 37;

    public static string ColorOf(int number) => number == 0 ? "green" : Red.Contains(number) ? "red" : "black";

    /// <summary>Index of a pocket in spin order (0 at position 0).</summary>
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
                            || Key is "firstfour";

    /// <summary>Payout multiplier applied to the stake when this target wins (stake is returned net).</summary>
    public int MultiplierFor(int result)
    {
        var t = Key.Trim().ToLowerInvariant();

        if (t.StartsWith("n-", StringComparison.Ordinal))
            return int.TryParse(t.AsSpan(2), out var n) && result == n ? 36 : 0;

        switch (t)
        {
            case "red": return result == 0 ? 0 : Wheel.ColorOf(result) == "red" ? 2 : 0;
            case "black": return result == 0 ? 0 : Wheel.ColorOf(result) == "black" ? 2 : 0;
            case "odd": return result == 0 ? 0 : (result % 2 == 1 ? 2 : 0);
            case "even": return result == 0 ? 0 : (result % 2 == 0 ? 2 : 0);
            case "low": return result is >= 1 and <= 18 ? 2 : 0;
            case "high": return result is >= 19 and <= 36 ? 2 : 0;
            case "firstfour": return result is >= 0 and <= 3 ? 9 : 0;
        }

        if (t.StartsWith("dozen-", StringComparison.Ordinal))
        {
            if (!int.TryParse(t.AsSpan(6), out var d) || d is < 1 or > 3) return 0;
            var dozen = result == 0 ? 0 : (result - 1) / 12 + 1;
            return dozen == d ? 3 : 0;
        }

        if (t.StartsWith("col-", StringComparison.Ordinal))
        {
            if (!int.TryParse(t.AsSpan(4), out var c) || c is < 1 or > 3) return 0;
            if (result == 0) return 0;
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
            if (!int.TryParse(p, out var n) || n is < 0 or > 36) return false;
            numbers.Add(n);
        }

        return true;
    }

    public override string ToString()
    {
        var t = Key;
        if (t.StartsWith("n-", StringComparison.Ordinal)) return $"Straight {t[2..]}";
        if (t.StartsWith("split-", StringComparison.Ordinal)) return $"Split {t[6..].Replace("-", "/")}";
        if (t.StartsWith("street-", StringComparison.Ordinal)) return $"Street {t[7..].Replace("-", "/")}";
        if (t.StartsWith("corner-", StringComparison.Ordinal)) return $"Corner {t[7..].Replace("-", "/")}";
        if (t.StartsWith("sixline-", StringComparison.Ordinal)) return $"Six Line {t[8..].Replace("-", "/")}";
        if (t.StartsWith("dozen-", StringComparison.Ordinal)) return $"Dozen {t[6..]}";
        if (t.StartsWith("col-", StringComparison.Ordinal)) return $"Column {t[4..]}";
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}

/// <summary>Verifies the server's provable-fairness proof for a spin.</summary>
public static class Fairness
{
    /// <summary>
    /// Verified formula: sha256("{dice_roll}:{control_number}"), first 8 hex chars as
    /// big-endian uint32 seed, result = seed % 37.
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
