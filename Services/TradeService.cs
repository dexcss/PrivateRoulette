using System;
using System.Collections.Generic;

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
            this.gilBefore = InventoryManager.Instance()->GetGil();
            (this.partnerId, this.partnerLabel) = this.ResolvePartner();
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
    }

    /// <summary>Framework tick: apply the trade a moment after the window closes.</summary>
    public void Tick()
    {
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
            this.chat.Print($"[trade] {delta:+#,0;-#,0} gil changed but the trade partner wasn't matched to a seated player — adjust manually.");
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
            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 64) continue;
                var match = this.session.FindPlayerByName(text.Trim());
                if (match != null) return (match.Value.Id, match.Value.Name);
            }

            // 2) fallback: string values
            for (var i = 0; i < addon->AtkValuesCount; i++)
            {
                string? text;
                try
                {
                    text = addon->AtkValues[i].GetValueAsString();
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text) || text.Length > 64) continue;

                var match = this.session.FindPlayerByName(text.Trim());
                if (match != null) return (match.Value.Id, match.Value.Name);
            }

            this.log.Debug($"trade partner not matched; window texts: {string.Join(" | ", texts)}");
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
