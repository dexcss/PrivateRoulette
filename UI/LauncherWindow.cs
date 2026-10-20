using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using PrivateRoulette.Configuration;

namespace PrivateRoulette.UI;

/// <summary>
/// Small always-available on-screen button that opens the dealer console.
/// This exists because the server-info-bar (DTR) entry can be hidden by the
/// player's HUD layout; this is a literal button that is always present.
/// </summary>
public sealed class LauncherWindow : Window
{
    private readonly PluginConfig config;
    private readonly Action openMain;

    public LauncherWindow(PluginConfig config, Action openMain)
        : base(
            "Private Roulette###prlauncher",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav)
    {
        this.config = config;
        this.openMain = openMain;
        this.Position = new Vector2(24, 300);
        this.PositionCondition = ImGuiCond.FirstUseEver;
        this.IsOpen = true;
        this.RespectCloseHotkey = false;
    }

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.79f, 0.64f, 0.15f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.75f, 0.24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.67f, 0.53f, 0.10f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.04f, 1f));
        var clicked = ImGui.Button("Dealer console", new Vector2(164, 28));
        ImGui.PopStyleColor(4);

        if (clicked)
            this.openMain();

        ImGui.SameLine(0, 6);
        if (ImGui.SmallButton("x"))
        {
            this.config.ShowLauncherButton = false;
            this.config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide this button. Re-enable it in /pr settings.");
    }
}
