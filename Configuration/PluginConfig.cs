using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace PrivateRoulette.Configuration;

public enum SpinMode
{
    /// <summary>Dealer rolls /random 0-999; server combines with the control number (provable).</summary>
    Control,

    /// <summary>Server RNG picks the pocket.</summary>
    Random,

    /// <summary>Dealer forces the exact number.</summary>
    Manual,
}

public sealed class CharacterApiKey
{
    [JsonProperty("apiKey")] public string ApiKey { get; set; } = string.Empty;
    [JsonProperty("characterName")] public string CharacterName { get; set; } = string.Empty;
}

public sealed class ChipPreset
{
    public string Name { get; set; } = "Standard";
    public List<int> Values { get; set; } = new() { 25, 100, 500, 1000 };
}

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---- identity / server ----
    /// <summary>Backend WebSocket URL (your Cloudflare Worker).</summary>
    [JsonProperty("ApiUrl")]
    public string ApiUrl { get; set; } = "wss://private-roulette-api.YOUR-SUBDOMAIN.workers.dev/ws";

    /// <summary>Dealer token — must match the backend's DEALER_TOKEN secret. This is the only credential.</summary>
    [JsonProperty("DealerToken")]
    public string DealerToken { get; set; } = string.Empty;

    // ---- access control ----
    /// <summary>Dealers allowed to use this plugin, normalized "Name@World" (case-insensitive).</summary>
    [JsonProperty("AuthorizedDealers")]
    public List<string> AuthorizedDealers { get; set; } = new() { "Akari Zanto@Adamantoise" };

    /// <summary>Normalizes any accepted spelling ("Name - World", "Name@World", whitespace/case) to "name@world".</summary>
    public static string NormalizeDealer(string entry)
    {
        var s = (entry ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;
        s = s.Replace(" - ", "@", StringComparison.Ordinal).Replace('-', '@');
        var parts = s.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => string.Empty,
            1 => parts[0].ToLowerInvariant(),
            _ => $"{parts[0]}@{parts[1]}".ToLowerInvariant(),
        };
    }

    public bool IsDealerAuthorized(string name, string world)
    {
        var key = NormalizeDealer($"{name}@{world}");
        return key.Length > 0 && this.AuthorizedDealers.Any(a => NormalizeDealer(a) == key);
    }

    // ---- game defaults ----
    [JsonProperty("SpinMode")] public SpinMode DefaultSpinMode { get; set; } = SpinMode.Control;
    [JsonProperty("MaxBetInner")] public int MaxBetInner { get; set; } = 500;
    [JsonProperty("MaxBetOuter")] public int MaxBetOuter { get; set; } = 250;
    [JsonProperty("MaxBetInnerVip")] public int MaxBetInnerVip { get; set; } = 1000;
    [JsonProperty("MaxBetOuterVip")] public int MaxBetOuterVip { get; set; } = 500;
    [JsonProperty("ChipPresets")]
    public List<ChipPreset> ChipPresets { get; set; } = new()
    {
        new() { Name = "Standard", Values = new() { 25, 100, 500, 1000 } },
        new() { Name = "High Roller", Values = new() { 100, 500, 1000, 5000 } },
        new() { Name = "Budget", Values = new() { 10, 25, 50, 100 } },
    };

    // ---- announcements (placeholders: $Gil $SpinType $Control $Bets $Name $PlayerBets $Number $Color $Results $Delta $Link $Balance $Message $Balances $Count) ----
    [JsonProperty("Announcements")]
    public Dictionary<string, string> Announcements { get; set; } = new()
    {
        ["GameInfo"] = "》》》 Game Information 《《《\n》 Maximum bet: $Gil gil\n》 Spin type: $SpinType",
        ["GameInfoControl"] = "》》》 Game Information 《《《\n》 Maximum bet: $Gil gil\n》 Spin type: $SpinType\n》 Control number: $Control",
        ["CurrentBets"] = "》》》 Current Bets: 《《《\n$Bets",
        ["PlayerBets"] = "$Name: $PlayerBets",
        ["Win"] = "》Number $Number ($Color) wins!",
        ["Results"] = "》》》 Results: 《《《\n$Results",
        ["PlayerDelta"] = "$Name: $Delta",
        ["PlayerLink"] = "Your Link: $Link",
        ["PlayerBalance"] = "Your Balance: $Balance",
        ["NoBalance"] = "You do not have any balance remaining",
        ["NoBets"] = "$Name has no bets",
        ["ControlNumber"] = "Control number for this game is $Control",
        ["LastCall"] = "Last call for bets!",
        ["Attention"] = "》》》 Attention 《《《\r\n$Message",
        ["Balances"] = "》》》 Player Balances: 《《《\r\n$Balances",
    };

    // ---- behaviour ----
    [JsonProperty("AutoAnnounceBets")] public bool AutoAnnounceBets { get; set; } = true;
    [JsonProperty("AutoAnnounceResults")] public bool AutoAnnounceResults { get; set; } = true;
    [JsonProperty("SoundEffects")] public bool SoundEffects { get; set; } = true;
    [JsonProperty("VerboseWireLog")] public bool VerboseWireLog { get; set; } = true;
    [JsonProperty("TradeThrottleMs")] public int TradeThrottleMs { get; set; } = 800;

    [JsonIgnore] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

    public void Save() => this.pluginInterface?.SavePluginConfig(this);
}
