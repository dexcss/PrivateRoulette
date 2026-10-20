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
    private readonly WheelWindow wheel;
    private readonly LauncherWindow launcher;
    private readonly PluginConfig config;
    private readonly IPluginLog log;

    public PluginUi(IDalamudPluginInterface pluginInterface, PluginConfig config, SocketService socket, RouletteSession session, IPluginLog log)
    {
        this.log = log;
        this.config = config;
        this.wheel = new WheelWindow(config, session, log);
        this.main = new MainWindow(config, socket, session, log, this.ShowWheel);
        this.wheel.OpenMain = this.OpenMain;
        this.settings = new SettingsWindow(config);
        this.launcher = new LauncherWindow(config, this.OpenMain);
        this.windowSystem.AddWindow(this.main);
        this.windowSystem.AddWindow(this.wheel);
        this.windowSystem.AddWindow(this.settings);
        this.windowSystem.AddWindow(this.launcher);
        pluginInterface.UiBuilder.Draw += this.DrawAll;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenMain;
    }

    private void DrawAll()
    {
        // Keep the launcher in sync with the settings checkbox.
        this.launcher.IsOpen = this.config.ShowLauncherButton;
        this.windowSystem.Draw();
    }

    public void ShowWheel()
    {
        this.wheel.IsOpen = true;
        this.wheel.BringToFront();
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
