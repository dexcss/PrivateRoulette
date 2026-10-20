using System;
using System.Text.RegularExpressions;

using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

using PrivateRoulette.Protocol;

namespace PrivateRoulette.Services;

/// <summary>
/// Parses dice-roll results from the chat log (EN/DE/FR/JP server messages)
/// and completes a pending control-mode spin. Also provides announcement
/// template rendering.
/// </summary>
public sealed partial class DiceService : IDisposable
{
    private readonly RouletteSession session;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    // EN: "Random! (1-1000) 530"  |  "Random! 530"  |  "Random! You roll a 437."
    [GeneratedRegex(@"^Random!\s*\(1-(\d{1,4})\)\s+(\d{1,4})$")]
    private static partial Regex EnRollFull();

    [GeneratedRegex(@"^Random!\s+You roll a\s+(\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EnRollYouRoll();

    [GeneratedRegex(@"^Random!\s+(\d{1,4})")]
    private static partial Regex EnRollShort();

    // DE: "Würfeln! (1-1000) 530"
    [GeneratedRegex(@"^Würfeln!\s*\(1-(\d{1,4})\)\s+(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DeRollFull();

    [GeneratedRegex(@"^Würfeln!\s+(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DeRollShort();

    // FR: "Lancer d'un dé (1000) ! 530" | "Lancer de dé ! 530"
    [GeneratedRegex(@"^Lancer d'un dé\s*\((\d{1,4})\)\s*!\s*(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex FrRollFull();

    [GeneratedRegex(@"^Lancer de dé\s*!\s*(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex FrRollShort();

    // JP (best effort — the original stored these mojibake-encoded): "サイコロ MAX(1000) 530"-shaped
    [GeneratedRegex(@"MAX(\d{1,4})\D{0,3}(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex JpRoll();

    /// <summary>Parses a dice roll value from a system chat line; null when the line is not a roll.</summary>
    public static long? ParseDiceRoll(string message)
    {
        var modern = EnRollYouRoll().Match(message);
        if (modern.Success && long.TryParse(modern.Groups[1].Value, out var mv))
            return MapRoll(mv);

        foreach (var rx in new[] { EnRollFull(), DeRollFull(), FrRollFull() })
        {
            var m = rx.Match(message);
            if (m.Success && long.TryParse(m.Groups[2].Value, out var v))
                return MapRoll(v);
        }

        foreach (var rx in new[] { EnRollShort(), DeRollShort(), FrRollShort() })
        {
            var m = rx.Match(message);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var v))
                return MapRoll(v);
        }

        var jp = JpRoll().Match(message);
        if (jp.Success && long.TryParse(jp.Groups[2].Value, out var jv))
            return MapRoll(jv);

        return null;
    }

    /// <summary>/random 1000 → 1..1000; map 1000 to 0 so the domain is exactly 0..999.</summary>
    private static long MapRoll(long value) => value == 1000 ? 0 : value;

    public DiceService(RouletteSession session, IChatGui chat, IClientState clientState, IPlayerState playerState, IPluginLog log)
    {
        this.session = session;
        this.chat = chat;
        this.clientState = clientState;
        this.playerState = playerState;
        this.log = log;

        clientState.Login += this.OnLogin;
        clientState.Logout += this.OnLogout;
        if (clientState.IsLoggedIn)
            this.RefreshCharacter();

        // watch chat for dice rolls
        chat.ChatMessage += this.OnChatMessage;
    }

    private void OnLogin()
    {
        this.RefreshCharacter();
        var hello = this.session.CurrentHello();
        if (hello != null)
            this.log.Info("character context ready for backend hello");
    }

    private void OnLogout(int kind, int code) => CharacterContext.Update(0, string.Empty, string.Empty);

    private void RefreshCharacter()
    {
        try
        {
            var contentId = this.playerState.ContentId;
            var name = this.playerState.CharacterName ?? string.Empty;
            var world = string.Empty;
            try
            {
                world = this.playerState.HomeWorld.Value.Name.ExtractText();
            }
            catch
            {
                // RowRef not ready yet; will refresh on next login/tick
            }

            CharacterContext.Update(contentId, name, world);
        }
        catch (Exception e)
        {
            this.log.Debug(e, "character refresh failed (not logged in yet)");
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        // The dice result line can arrive as SystemMessage/Echo depending on
        // client language and settings; the regexes are specific enough that
        // we can safely test every chat line.
        var text = message.Message.TextValue;
        var roll = ParseDiceRoll(text);
        if (roll == null)
        {
            if (text.Contains("Random", StringComparison.OrdinalIgnoreCase))
                this.log.Debug($"[dice] unparsed roll line: {text}");
            return;
        }

        this.log.Info($"dice roll detected: {roll} ({text})");
        if (this.session.AwaitingDice && this.session.PendingSpin != null)
        {
            this.log.Info($"completing spin with dice roll {roll}");
            this.session.Spin(roll, null);
        }
    }

    public void Dispose()
    {
        this.chat.ChatMessage -= this.OnChatMessage;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
    }
}
