using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.NativeWrapper;
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
/// Partner identification, in order of reliability:
///   1. text nodes of the open trade window (name is rendered there)
///   2. the dealer's current target
///   3. the most recent player the dealer targeted (covers "target, trade, target someone else")
/// The gil delta itself is exact: dealer gil is snapshotted at open and read
/// again shortly after the window closes.
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
    private AtkUnitBasePtr addonPtr;
    private string lastWindowSample = string.Empty;
    private string? lastTargetName;
    private DateTime lastTargetAt = DateTime.MinValue;

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
            this.lastWindowSample = string.Empty;
            this.addonPtr = args.Addon;
            this.gilBefore = InventoryManager.Instance()->GetGil();

            this.ResolvePartnerWithFallbacks();
            this.log.Debug($"trade opened: gil {this.gilBefore}, addon {this.addonPtr.Address:X}, partner {this.partnerLabel ?? "unknown"} ({this.partnerId ?? "-"})");
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

        if (this.partnerId == null)
            this.ResolvePartnerWithFallbacks();
    }

    /// <summary>Framework tick: remember player targets and apply the trade shortly after close.</summary>
    public void Tick()
    {
        // remember the most recent player target — the usual "add targeted, then trade" flow
        var target = this.session.GetTargetPlayerName?.Invoke();
        if (!string.IsNullOrEmpty(target))
        {
            this.lastTargetName = target;
            this.lastTargetAt = DateTime.UtcNow;
            if (this.tradeOpen && this.partnerId == null)
                this.TryResolveFromTarget();
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
            var sample = this.lastWindowSample.Length > 160 ? this.lastWindowSample[..160] : this.lastWindowSample;
            this.chat.Print($"[trade] {delta:+#,0;-#,0} gil changed but the partner wasn't matched — target the player before trading, or adjust manually.");
            this.log.Info($"[trade] unmatched. window='{sample}' target='{this.lastTargetName ?? "none"}' seated='{this.SeatedNames()}'");
            return;
        }

        this.session.ApplyTradeDelta(this.partnerId, delta);
        this.chat.Print($"[trade] {this.partnerLabel}: {delta:+#,0;-#,0} gil applied to balance.");
    }

    private string SeatedNames()
    {
        var players = this.session.State?.Players;
        return players == null ? string.Empty : string.Join(", ", players.Select(p => p.Name));
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

    private void TryResolveFromRememberedTarget()
    {
        if (this.lastTargetName == null) return;
        if ((DateTime.UtcNow - this.lastTargetAt).TotalSeconds > 180) return;
        var match = this.session.FindPlayerByName(this.lastTargetName);
        if (match != null)
        {
            this.partnerId = match.Value.Id;
            this.partnerLabel = match.Value.Name;
        }
    }

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
        if (this.partnerId != null) return;
        this.TryResolveFromRememberedTarget();
    }

    /// <summary>Find a seated player whose name appears in the trade window.</summary>
    private (string? id, string? label) ResolvePartner()
    {
        try
        {
            AtkUnitBase* addon = this.addonPtr.Address != 0
                ? (AtkUnitBase*)this.addonPtr.Address
                : (AtkUnitBase*)this.gameGui.GetAddonByName("Trade", 1).Address;
            if (addon == null) return (null, null);

            var texts = new List<string>(64);

            // window title (node 2 is the title text in most addons)
            try
            {
                var titleNode = addon->GetTextNodeById(2);
                if (titleNode != null)
                {
                    var title = titleNode->NodeText.ToString();
                    if (!string.IsNullOrWhiteSpace(title)) texts.Add(title);
                }
            }
            catch { }

            // every node registered with the ULD manager
            try
            {
                var list = addon->UldManager.NodeList;
                var count = addon->UldManager.NodeListCount;
                for (var i = 0; i < count && i < 400; i++)
                {
                    var node = list[i];
                    if (node == null || node->Type != NodeType.Text) continue;
                    var text = ((AtkTextNode*)node)->NodeText.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                }
            }
            catch { }

            // recursive walk as a safety net
            this.WalkTextNodes(addon->RootNode, texts);

            // string values
            var trade = (AddonTrade*)addon;
            for (var i = 0; i < trade->AtkValuesCount; i++)
            {
                try
                {
                    var s = trade->AtkValues[i].GetValueAsString();
                    if (!string.IsNullOrWhiteSpace(s)) texts.Add(s);
                }
                catch { }
            }

            foreach (var raw in texts)
            {
                var text = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (text.Length < 3 || text.Length > 80) continue;

                var exact = this.session.FindPlayerByName(text);
                if (exact != null) return (exact.Value.Id, exact.Value.Name);

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
                .Take(6));
            this.log.Debug($"trade window scan: nodes={addon->UldManager.NodeListCount}, values={trade->AtkValuesCount}, texts='{this.lastWindowSample}'");
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
            catch { }
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
