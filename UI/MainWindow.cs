using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;
using PrivateRoulette.Services;

namespace PrivateRoulette.UI;

/// <summary>Main dealer console window.</summary>
public sealed class MainWindow : Window
{
    private readonly PluginConfig config;
    private readonly SocketService socket;
    private readonly RouletteSession session;
    private readonly IPluginLog log;

    private string newPlayerName = string.Empty;
    private int newPlayerBankrollInt;
    private string diceInput = string.Empty;
    private string manualNumberInput = string.Empty;
    private SpinMode selectedMode = SpinMode.Control;
    private string apiKeyInput = string.Empty;
    private int adjustAmountInt = 100;

    public MainWindow(PluginConfig config, SocketService socket, RouletteSession session, IPluginLog log)
        : base("Private Roulette — Dealer Console")
    {
        this.config = config;
        this.socket = socket;
        this.session = session;
        this.log = log;

        this.Size = new Vector2(760, 620);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    public override void Draw()
    {
        if (!this.session.IsAuthorized)
        {
            this.DrawLockedPanel();
            return;
        }

        this.DrawStatusHeader();
        ImGui.Separator();

        if (!this.session.Authenticated)
        {
            this.DrawAuthPanel();
            return;
        }

        if (this.session.GameId == null)
        {
            this.DrawLobby();
            return;
        }

        // two-column: table state | controls — both scroll independently
        if (ImGui.BeginTable("mainCols", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("##left", ImGuiTableColumnFlags.WidthStretch, 0.62f);
            ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.BeginChild("leftcol", new Vector2(0, 0), false);
            this.DrawPlayers();
            ImGui.EndChild();
            ImGui.TableNextColumn();
            ImGui.BeginChild("rightcol", new Vector2(0, 0), false);
            this.DrawSpinControls();
            this.DrawGameControls();
            ImGui.EndChild();
            ImGui.EndTable();
        }
    }

    /// <summary>Shown to any character not on the authorized dealer list.</summary>
    private void DrawLockedPanel()
    {
        ImGui.Dummy(new Vector2(0, 24));
        ImGui.TextColored(ImGuiColors.DalamudRed, "PRIVATE ROULETTE");
        ImGui.TextDisabled("This build is restricted to authorized dealers only.");
        ImGui.Dummy(new Vector2(0, 8));

        var (_, name, world) = CharacterContext.Current;
        if (name.Length > 0)
        {
            ImGui.Text("Current character:");
            ImGui.SameLine();
            ImGui.TextUnformatted(world.Length > 0 ? $"{name}@{world}" : name);

            ImGui.TextColored(ImGuiColors.DalamudYellow, this.session.AuthorizationNote);
        }
        else
        {
            ImGui.TextDisabled(this.session.AuthorizationNote);
        }

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.TextDisabled("Authorized dealers:");
        foreach (var entry in this.config.AuthorizedDealers)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(entry);
        }

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.TextDisabled("The socket connection is never opened for unauthorized characters.");
    }

    private void DrawStatusHeader()
    {
        var connected = this.socket.IsConnected;
        var authed = this.session.Authenticated;
        var color = !connected ? ImGuiColors.DalamudRed : authed ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow;
        var status = !connected ? "● offline" : authed ? "● authenticated" : "● connected, authenticating…";
        ImGui.TextColored(color, status);
        ImGui.SameLine();
        if (!string.IsNullOrEmpty(this.session.AuthMessage))
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, this.session.AuthMessage);
        }

        if (ImGui.BeginPopupContextItem("statusMenu"))
        {
            if (ImGui.MenuItem("Reconnect"))
                _ = this.socket.StartAsync();
            ImGui.EndPopup();
        }

        if (this.session.GameId != null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"game {this.session.GameId}");
            if (this.session.State?.ControlNumber != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.ParsedBlue, $"control #{this.session.State.ControlNumber}");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("← Tables"))
                this.session.LeaveTable();
        }
    }

    private void DrawAuthPanel()
    {
        var (_, name, world) = CharacterContext.Current;
        ImGui.TextUnformatted("Backend authentication");
        ImGui.TextDisabled($"{name}@{world}");

        ImGui.TextDisabled($"Backend: {this.config.ApiUrl}");
        ImGui.Separator();

        var token = this.session.DealerToken;
        if (string.IsNullOrEmpty(token))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
            ImGui.TextUnformatted("No dealer token configured.");
            ImGui.PopStyleColor();
            // NOTE: do not copy config → input here; that would wipe the user's
            // typing every frame. apiKeyInput starts empty and is saved on click.
            ImGui.TextDisabled("Paste your dealer token below (run Get-Content $env:TEMP\\opencode\\pr_dealer_token.txt to see it):");
            ImGui.InputText("##token", ref apiKeyInput, 128, ImGuiInputTextFlags.Password);
            if (ImGui.Button("Save token") && !string.IsNullOrWhiteSpace(apiKeyInput))
            {
                this.session.SetDealerToken(apiKeyInput.Trim());
                apiKeyInput = string.Empty;
                // connect immediately instead of waiting out the retry backoff
                _ = this.socket.StartAsync();
            }
        }
        else
        {
            ImGui.TextDisabled("Dealer token configured.");
            if (ImGui.Button("Reconnect"))
                _ = this.socket.StartAsync();
        }
    }

    private void DrawLobby()
    {
        ImGui.TextUnformatted("No active game");

        SectionText("Create game");
        var modeInt = (int)selectedMode;
        if (ImGui.Combo("Spin mode", ref modeInt, "Control (provable)\0Random (server)\0Manual\0"))
            selectedMode = (SpinMode)modeInt;
        var maxInner = (int)this.config.MaxBetInner;
        var maxOuter = (int)this.config.MaxBetOuter;
        if (ImGui.InputInt("Max bet (inside)", ref maxInner))
            this.config.MaxBetInner = maxInner;
        if (ImGui.InputInt("Max bet (outside)", ref maxOuter))
            this.config.MaxBetOuter = maxOuter;
        if (ImGui.Button($"Create {selectedMode} game##createGame"))
        {
            this.session.CreateGame(selectedMode);
        }

        SectionText("Join existing game");
        foreach (var g in this.session.KnownGames)
        {
            var label = $"{g.Id} — {g.CreatorName}@{g.CreatorHomeworld} ({g.Players} players, {g.Status})";
            ImGui.Selectable(label, false);
            if (ImGui.IsItemClicked())
            {
                this.session.JoinGame(g.Id);
                return;
            }
        }

        // create-game inputs wrote into config directly; persist on change
        this.config.Save();
    }

    private void DrawPlayers()
    {
        var state = this.session.State;
        if (state == null)
        {
            ImGui.TextUnformatted("Waiting for table state…");
            return;
        }

        SectionText($"Players ({state.Players.Count})");
        if (ImGui.BeginTable("players", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Balance", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Bets", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var p in state.Players)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ColorFromHex(p.Color), p.DisplayName);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(p.Name);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{p.Balance:N0}");

                ImGui.TableNextColumn();
                var total = p.Bets.Values.Sum();
                ImGui.TextUnformatted(total > 0 ? $"{total:N0} ({p.Bets.Count})" : "—");

                ImGui.TableNextColumn();
                var flags = new System.Collections.Generic.List<string>();
                if (p.Ready) flags.Add("ready");
                if (p.Away) flags.Add("away");
                if (p.IsVip) flags.Add("vip");
                if (p.IsFreeplay) flags.Add("free");
                ImGui.TextUnformatted(flags.Count > 0 ? string.Join(" ", flags) : "");

                ImGui.TableNextColumn();
                ImGui.PushID(p.Id);
                if (ImGui.Button("+100"))
                    this.session.AdjustBalance(p.Id, 100);
                ImGui.SameLine();
                if (ImGui.Button("-100"))
                    this.session.AdjustBalance(p.Id, -100);
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy link"))
                {
                    var link = this.session.PlayerLink(p);
                    if (link != null)
                        ImGui.SetClipboardText(link);
                }
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.InputText("Name (Char@World or display)", ref newPlayerName, 64);
        ImGui.InputInt("Buy-in", ref newPlayerBankrollInt);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(newPlayerName));
        if (ImGui.Button("Add player"))
            this.session.AddPlayer(newPlayerName.Trim(), newPlayerName.Trim().Split('@')[0], newPlayerBankrollInt);
        ImGui.EndDisabled();

        SectionText("Last spin");
        var spin = this.session.LastSpin;
        if (spin != null)
        {
            ImGui.TextColored(ColorFromHex(Wheel.ColorOf(spin.Result) switch
            {
                "red" => "#E74C3C",
                "black" => "#B0B0B0",
                _ => "#2ECC71",
            }), $"{spin.Result} {spin.Color.ToUpperInvariant()}");
            if (spin.Proof != null)
            {
                var (valid, expected, seed, hash) = Fairness.Verify(spin.Proof.DiceRoll ?? -1, spin.Proof.ControlNumber, spin.Result);
                ImGui.TextDisabled($"proof seed={seed} hash={hash}");
                ImGui.TextColored(valid ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                    valid ? "fairness verified ✓" : $"MISMATCH — expected {expected}!");
            }
        }
        else
        {
            ImGui.TextDisabled("no spins yet");
        }

        SectionText("Table chat");
        this.DrawTableChat();

        SectionText("Ledger");
        this.DrawLedger();
    }

    private void DrawTableChat()
    {
        ImGui.BeginChild("tablechat", new Vector2(0, 110), true);;
        foreach (var m in this.session.Chat.TakeLast(30))
        {
            ImGui.TextColored(ColorFromHex(m.Color), m.FromName + ":");
            ImGui.SameLine();
            ImGui.TextWrapped(m.Text);
        }
        if (this.session.Chat.Count == 0)
            ImGui.TextDisabled("(table chat shows here — players see it on the web client)");
        ImGui.EndChild();
    }

    private void DrawLedger()
    {
        var ledger = this.session.LastLedger;
        if (ledger == null)
        {
            ImGui.TextDisabled("press Refresh ledger to load the server ledger");
            return;
        }

        if (ledger.Summary is { } summary)
        {
            ImGui.TextUnformatted($"rounds: {summary.GetValueOrDefault("total_rounds")} · bets: {FormatGil(summary.GetValueOrDefault("total_buy_ins"))} · tips: {FormatGil(summary.GetValueOrDefault("total_tips"))}");
        }

        if (ledger.Rounds is { Count: > 0 })
        {
            if (ImGui.BeginTable("ledgerRounds", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("Round", ImGuiTableColumnFlags.WidthFixed, 46);
                ImGui.TableSetupColumn("Num", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 58);
                ImGui.TableSetupColumn("Bets", ImGuiTableColumnFlags.WidthFixed, 78);
                ImGui.TableSetupColumn("Payouts", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var r in ledger.Rounds.TakeLast(8))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.GetValueOrDefault("round_number")}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.GetValueOrDefault("winning_number")}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.GetValueOrDefault("winning_color")}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatGil(r.GetValueOrDefault("total_bets")));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatGil(r.GetValueOrDefault("total_payouts")));
                }
                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.TextDisabled("no rounds played yet");
        }
    }

    private static string FormatGil(object? v)
    {
        try { return Convert.ToInt64(v).ToString("N0"); }
        catch { return "0"; }
    }

    private void DrawSpinControls()
    {
        SectionText("Spin");
        var state = this.session.State;
        if (state == null) return;

        if (state.Spinning)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "wheel is spinning…");
            return;
        }

        if (this.session.PendingSpin != null)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "table locked — waiting for dice");
            ImGui.TextDisabled($"control #{this.session.PendingSpin.ControlNumber}");
            ImGui.SetNextItemWidth(140);
            ImGui.InputText("dice (0-999)", ref diceInput, 8);
            ImGui.SameLine();
            ImGui.BeginDisabled(!long.TryParse(diceInput, out var dr) || dr < 0 || dr > 999);
            if (ImGui.Button("Spin!"))
            {
                this.session.Spin(dr, null);
                diceInput = string.Empty;
            }
            ImGui.EndDisabled();
            ImGui.TextDisabled("or roll /random in chat — it will be picked up automatically");
        }
        else
        {
            if (ImGui.Button("Request spin (lock table)"))
                this.session.RequestSpin();
        }

        if (state.SpinMode == "manual")
        {
            ImGui.SetNextItemWidth(100);
            ImGui.InputText("number (0-36)", ref manualNumberInput, 4);
            ImGui.SameLine();
            ImGui.BeginDisabled(!int.TryParse(manualNumberInput, out var mn) || mn is < 0 or > 36);
            if (ImGui.Button("Force number"))
                this.session.Spin(null, mn);
            ImGui.EndDisabled();
        }
    }

    private void DrawGameControls()
    {
        SectionText("Table");
        if (ImGui.Button("Clear bets"))
            this.session.ClearBets();
        ImGui.SameLine();
        if (ImGui.Button("Rebet"))
            this.session.Rebet();
        ImGui.SameLine();
        if (ImGui.Button("Refresh ledger"))
            this.session.RefreshLedger();

        SectionText("Announcements");
        if (ImGui.Button("Game info"))
        {
            var st = this.session.State;
            this.AnnounceNow("GameInfoControl",
                ("Gil", $"{this.config.MaxBetInner:N0}"),
                ("SpinType", st?.SpinMode ?? "control"),
                ("Control", st?.ControlNumber ?? "?"));
        }
        ImGui.SameLine();
        if (ImGui.Button("Last call"))
            this.AnnounceNow("LastCall");

        SectionText("Adjust balance");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##adjAmt", ref adjustAmountInt);
        ImGui.TextDisabled("use the +100/-100 buttons next to each player");

        SectionText("Table chat");
        ImGui.TextDisabled("table chat + dealer stats arrive in Phase 2");
    }

    internal static void SectionText(string text)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(text);
        ImGui.Spacing();
    }

    private void AnnounceNow(string key, params (string, string)[] placeholders)
    {
        // announcements flow through DiceService; expose directly here for the skeleton
        var template = this.config.Announcements.GetValueOrDefault(key);
        if (template == null) return;
        foreach (var (k, v) in placeholders)
            template = template.Replace($"${k}", v, StringComparison.Ordinal);
        if (this.session.GameId != null)
            this.session.SendChat(template);
    }

    internal static Vector4 ColorFromHex(string hex)
    {
        try
        {
            if (hex.StartsWith('#')) hex = hex[1..];
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        }
        catch
        {
            return new Vector4(1, 1, 1, 1);
        }
    }
}

/// <summary>Settings window.</summary>
public sealed class SettingsWindow : Window
{
    private readonly PluginConfig config;
    private string newDealer = string.Empty;

    public SettingsWindow(PluginConfig config) : base("Private Roulette — Settings")
    {
        this.config = config;
        this.Size = new Vector2(560, 480);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        MainWindow.SectionText("Authorized dealers");
        ImGui.TextDisabled("Only these characters can use this plugin. Format: Name@World (or Name - World).");
        var toRemove = -1;
        for (var i = 0; i < this.config.AuthorizedDealers.Count; i++)
        {
            ImGui.PushID($"dealer_{i}");
            ImGui.TextUnformatted(this.config.AuthorizedDealers[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                toRemove = i;
            ImGui.PopID();
        }

        if (toRemove >= 0)
        {
            this.config.AuthorizedDealers.RemoveAt(toRemove);
            this.config.Save();
        }

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("##newDealer", ref newDealer, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add") && newDealer.Trim().Length > 0)
        {
            var normalized = PluginConfig.NormalizeDealer(newDealer);
            if (normalized.Length > 0 && !this.config.AuthorizedDealers.Any(a => PluginConfig.NormalizeDealer(a) == normalized))
            {
                this.config.AuthorizedDealers.Add(normalized);
                this.config.Save();
            }

            newDealer = string.Empty;
        }

        MainWindow.SectionText("Backend");
        var apiUrl = this.config.ApiUrl;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##apiurl", ref apiUrl, 256))
        {
            this.config.ApiUrl = apiUrl;
            this.config.Save();
        }
        ImGui.TextDisabled("Your Cloudflare Worker WebSocket endpoint.");

        var verbose = this.config.VerboseWireLog;
        if (ImGui.Checkbox("Verbose wire log (debug)", ref verbose))
        {
            this.config.VerboseWireLog = verbose;
            this.config.Save();
        }

        var sounds = this.config.SoundEffects;
        if (ImGui.Checkbox("Sound effects (Phase 2)", ref sounds))
        {
            this.config.SoundEffects = sounds;
            this.config.Save();
        }

        MainWindow.SectionText("Bet limits");
        int mi = (int)this.config.MaxBetInner, mo = (int)this.config.MaxBetOuter;
        int miv = (int)this.config.MaxBetInnerVip, mov = (int)this.config.MaxBetOuterVip;
        if (ImGui.InputInt("Max inside", ref mi) | ImGui.InputInt("Max outside", ref mo) |
            ImGui.InputInt("Max inside (VIP)", ref miv) | ImGui.InputInt("Max outside (VIP)", ref mov))
        {
            this.config.MaxBetInner = mi;
            this.config.MaxBetOuter = mo;
            this.config.MaxBetInnerVip = miv;
            this.config.MaxBetOuterVip = mov;
            this.config.Save();
        }

        MainWindow.SectionText("Announcement templates");
        ImGui.TextDisabled("Placeholders: $Gil $SpinType $Control $Bets $Name $PlayerBets $Number $Color $Results $Delta $Link $Balance $Message $Balances $Count");
        foreach (var key in this.config.Announcements.Keys.ToList())
        {
            var val = this.config.Announcements[key];
            if (ImGui.InputTextMultiline($"##ann_{key}", ref val, 512, new Vector2(-1, 44)))
            {
                this.config.Announcements[key] = val;
                this.config.Save();
            }
        }
    }
}
