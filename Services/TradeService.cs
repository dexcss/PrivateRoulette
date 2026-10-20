using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using PrivateRoulette.Configuration;

namespace PrivateRoulette.Services;

/// <summary>
/// Detects completed trades and applies the gil delta to the partner's balance.
///
/// How it works: when the Trade addon opens we snapshot the dealer's gil and
/// identify the partner from the trade window's AtkValues (matched against
/// seated players). When the addon closes we read the gil again — the delta is
/// exactly what changed hands — and send it as an adjustment ("Trade").
/// </summary>
public sealed unsafe class TradeService : IDisposable
{
    private readonly PluginConfig config;
    private readonly RouletteSession session;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private bool tradeOpen;
    private uint gilBefore;
    private string? partnerId;
    private string? partnerLabel;
    private bool pendingClose;
    private DateTime closedAt;
    private DateTime lastPartnerAttempt = DateTime.MinValue;
    private string lastWindowSample = string.Empty;

    public TradeService(
        PluginConfig config,
        RouletteSession session,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IChatGui chat,
        IPluginLog log)
    {
        this.config = config;
        this.session = session;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.chat = chat;
        this.log = log;

        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Trade", this.OnTradeSetup);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Trade", this.OnTradeClose);
    }

    private void OnTradeSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            this.tradeOpen = true;
            this.pendingClose = false;
            this.partnerId = null;
            this.partnerLabel = null;
            this.lastPartnerAttempt = DateTime.MinValue;
            this.lastWindowSample = string.Empty;
            this.gilBefore = InventoryManager.Instance()->GetGil();

            // target fallback first (the dealer usually just added them)
            this.TryResolveFromTarget();
            this.log.Debug($"trade opened: gil {this.gilBefore}, partner {this.partnerLabel ?? "unknown"} ({this.partnerId ?? "-"})");
        }
        catch (Exception e)
        {
            this.log.Error(e, "trade setup failed");
        }
    }

    private void OnTradeClose(AddonEvent type, AddonArgs args)
    {
        if (!this.tradeOpen) return;
        this.tradeOpen = false;
        this.pendingClose = true;
        this.closedAt = DateTime.UtcNow;

        // final attempts while the window still exists
        if (this.partnerId == null)
            this.ResolvePartnerWithFallbacks();
    }

    private void TryResolveFromTarget()
    {
        try
        {
            var target = this.session.GetTargetPlayerName?.Invoke();
            if (string.IsNullOrEmpty(target)) return;
            var match = this.session.FindPlayerByName(target);
            if (match != null)
            {
                this.partnerId = match.Value.Id;
                this.partnerLabel = match.Value.Name;
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Window text scan, then current-target fallback.</summary>
    private void ResolvePartnerWithFallbacks()
    {
        if (this.partnerId != null) return;
        var (id, label) = this.ResolvePartner();
        if (id != null)
        {
            this.partnerId = id;
            this.partnerLabel = label;
            return;
        }

        this.TryResolveFromTarget();
    }

    /// <summary>Framework tick: apply the trade a moment after the window closes.</summary>
    public void Tick()
    {
        // the trade window fills in its name a moment after opening — keep trying
        if (this.tradeOpen && this.partnerId == null &&
            (DateTime.UtcNow - this.lastPartnerAttempt).TotalMilliseconds > 250)
        {
            this.lastPartnerAttempt = DateTime.UtcNow;
            this.ResolvePartnerWithFallbacks();
        }

        if (!this.pendingClose) return;
        if ((DateTime.UtcNow - this.closedAt).TotalMilliseconds < 450) return;
        this.pendingClose = false;

        uint gilAfter;
        try
        {
            gilAfter = InventoryManager.Instance()->GetGil();
        }
        catch (Exception e)
        {
            this.log.Error(e, "trade gil read failed");
            return;
        }

        var delta = (long)gilAfter - this.gilBefore;
        if (delta == 0) return;

        if (this.partnerId == null)
        {
            var sample = this.lastWindowSample.Length > 120 ? this.lastWindowSample[..120] : this.lastWindowSample;
            this.chat.Print($"[trade] {delta:+#,0;-#,0} gil changed but the partner wasn't matched{(sample.Length > 0 ? $" (window: {sample})" : string.Empty)} — target the player and trade again, or adjust manually.");
            return;
        }

        this.session.ApplyTradeDelta(this.partnerId, delta);
        this.chat.Print($"[trade] {this.partnerLabel}: {delta:+#,0;-#,0} gil applied to balance.");
    }

    /// <summary>Find a seated player whose name appears in the trade window.</summary>
    private (string? id, string? label) ResolvePartner()
    {
        try
        {
            var addon = this.gameGui.GetAddonByName<AddonTrade>("Trade", 1);
            if (addon == null) return (null, null);

            // 1) walk all text nodes — the partner name is rendered as text
            var texts = new List<string>(32);
            this.WalkTextNodes(addon->AtkUnitBase.RootNode, texts);

            // 2) add string values as another source
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                try
                {
                    var s = addon->AtkValues[i].GetValueAsString();
                    if (!string.IsNullOrWhiteSpace(s)) texts.Add(s);
                }
                catch
                {
                    // non-string value
                }
            }

            foreach (var raw in texts)
            {
                var text = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (text.Length < 3 || text.Length > 80) continue;

                // exact match against full or display name
                var exact = this.session.FindPlayerByName(text);
                if (exact != null) return (exact.Value.Id, exact.Value.Name);

                // partial match (handles prefixes like "Trade with ...")
                var players = this.session.State?.Players;
                if (players == null) continue;
                foreach (var p in players)
                {
                    var shortName = p.Name.Split('@')[0];
                    if (shortName.Length < 3) continue;
                    if (text.Contains(shortName, StringComparison.OrdinalIgnoreCase) ||
                        shortName.Contains(text, StringComparison.OrdinalIgnoreCase))
                        return (p.Id, p.DisplayName);
                }
            }

            this.lastWindowSample = string.Join(" | ", texts
                .Select(t => new string(t.Where(c => !char.IsControl(c)).ToArray()).Trim())
                .Where(t => t.Length is > 0 and < 60)
                .Take(4));
            this.log.Debug($"trade partner not matched; window texts: {this.lastWindowSample}");
        }
        catch (Exception e)
        {
            this.log.Debug(e, "trade partner scan failed");
        }

        return (null, null);
    }

    private void WalkTextNodes(AtkResNode* node, List<string> sink)
    {
        if (node == null || sink.Count > 400) return;

        if (node->Type == NodeType.Text)
        {
            try
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrEmpty(text)) sink.Add(text);
            }
            catch
            {
                // ignore malformed nodes
            }
        }

        this.WalkTextNodes(node->ChildNode, sink);
        this.WalkTextNodes(node->NextSiblingNode, sink);
    }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Trade", this.OnTradeSetup);
        this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Trade", this.OnTradeClose);
    }
}
