using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;

namespace PrivateRoulette.Services;

/// <summary>
/// Parses dice-roll results from the chat log (EN/DE/FR/JP server messages)
/// and completes a pending control-mode spin. Also provides announcement
/// template rendering.
/// </summary>
public sealed partial class DiceService : IDisposable
{
    private readonly PluginConfig config;
    private readonly RouletteSession session;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    // Recovered from the original plugin's constant pool (see dumps/decoded_strings.txt).
    // EN: "Random! (1-1000) 530"  |  "Random! 530"
    [GeneratedRegex(@"^Random! \(1-(\d{1,3})\) (\d{1,3})$")]
    private static partial Regex EnRollFull();

    [GeneratedRegex(@"^Random! (\d{1,3})")]
    private static partial Regex EnRollShort();

    // DE: "Würfeln! (1-1000) 530"
    [GeneratedRegex(@"^Würfeln! \(1-(\d{1,3})\) (\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex DeRollFull();

    [GeneratedRegex(@"^Würfeln!\s+(\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex DeRollShort();

    // FR: "Lancer d'un dé (1000) ! 530" | "Lancer de dé ! 530"
    [GeneratedRegex(@"^Lancer d'un dé (\d+) ! (\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrRollFull();

    [GeneratedRegex(@"^Lancer de dé ! (\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrRollShort();

    // JP (best effort — the original stored these mojibake-encoded): "サイコロ MAX(1000) 530"-shaped
    [GeneratedRegex(@"MAX(\d{1,4})\D{0,3}(\d{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex JpRoll();

    /// <summary>Parses a dice roll value from a system chat line; null when the line is not a roll.</summary>
    public static long? ParseDiceRoll(string message)
    {
        foreach (var rx in new[] { EnRollFull(), DeRollFull(), FrRollFull() })
        {
            var m = rx.Match(message);
            if (m.Success && long.TryParse(m.Groups[2].Value, out var v))
                return v;
        }

        foreach (var rx in new[] { EnRollShort(), DeRollShort(), FrRollShort() })
        {
            var m = rx.Match(message);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var v))
                return v;
        }

        var jp = JpRoll().Match(message);
        if (jp.Success && long.TryParse(jp.Groups[2].Value, out var jv))
            return jv;

        return null;
    }

    /// <summary>Rendered announcements waiting to be sent to the table chat.</summary>
    private readonly ConcurrentQueue<string> outgoingAnnouncements = new();

    public DiceService(PluginConfig config, RouletteSession session, IChatGui chat, IClientState clientState, IPlayerState playerState, IPluginLog log)
    {
        this.config = config;
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
        chat.CheckMessageHandled += this.OnChatMessage;
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
        // only system-ish messages can be rolls
        if (message.LogKind is not (XivChatType.SystemMessage or XivChatType.SystemError or XivChatType.Echo))
            return;

        var text = message.Message.TextValue;
        var roll = ParseDiceRoll(text);
        if (roll == null)
            return;

        this.log.Info($"dice roll detected: {roll} ({text})");
        if (this.session.AwaitingDice && this.session.PendingSpin != null)
        {
            this.log.Info($"completing spin with dice roll {roll}");
            this.session.Spin(roll, null);
        }
    }

    /// <summary>Runs on the framework tick; flushes queued announcements into the table chat.</summary>
    public void Tick()
    {
        while (this.outgoingAnnouncements.TryDequeue(out var text))
        {
            if (this.session.GameId != null)
                this.session.SendChat(text);
        }
    }

    public void Announce(string templateKey, params (string key, string value)[] placeholders)
    {
        if (!this.config.Announcements.TryGetValue(templateKey, out var template))
            return;
        foreach (var (k, v) in placeholders)
            template = template.Replace($"${k}", v, StringComparison.Ordinal);
        this.outgoingAnnouncements.Enqueue(template);
    }

    public void Dispose()
    {
        this.chat.CheckMessageHandled -= this.OnChatMessage;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
    }
}
