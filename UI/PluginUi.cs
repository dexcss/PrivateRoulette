using System;

using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Services;

namespace PrivateRoulette.UI;

/// <summary>Owns and draws all windows.</summary>
public sealed class PluginUi : IDisposable
{
    private readonly WindowSystem windowSystem = new("PrivateRoulette");
    private readonly MainWindow main;
    private readonly SettingsWindow settings;
    private readonly IPluginLog log;

    public PluginUi(IDalamudPluginInterface pluginInterface, PluginConfig config, SocketService socket, RouletteSession session, IPluginLog log)
    {
        this.log = log;
        this.main = new MainWindow(config, socket, session, log);
        this.settings = new SettingsWindow(config);
        this.windowSystem.AddWindow(this.main);
        this.windowSystem.AddWindow(this.settings);
        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
    }

    public void OpenMain()
    {
        this.main.IsOpen = true;
        this.main.BringToFront();
    }

    public void OpenSettings()
    {
        this.settings.IsOpen = true;
        this.settings.BringToFront();
    }

    public void Draw()
    {
        // windowSystem is wired to UiBuilder.Draw directly; kept for future overlays
    }

    public void Dispose()
    {
        this.windowSystem.RemoveAllWindows();
    }
}
