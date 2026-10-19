using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;
using PrivateRoulette.Services;
using PrivateRoulette.UI;

namespace PrivateRoulette;

/// <summary>
/// Plugin entry point. Wires services together and owns the framework tick.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/pr";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly PluginConfig config;
    private readonly SocketService socket;
    private readonly RouletteSession session;
    private readonly DiceService dice;
    private readonly PluginUi ui;

    /// <summary>Work items marshalled from socket threads onto the framework thread.</summary>
    private readonly ConcurrentQueue<Action> mainThreadQueue = new();

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework, IPluginLog log, IChatGui chatGui, IClientState clientState, IPlayerState playerState)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.log = log;

        this.config = this.pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        this.config.Initialize(this.pluginInterface);

        this.socket = new SocketService(this.config, log);
        this.session = new RouletteSession(this.config, this.socket, log, chatGui);
        this.dice = new DiceService(this.config, this.session, chatGui, clientState, playerState, log);
        this.ui = new PluginUi(this.pluginInterface, this.config, this.socket, this.session, log);

        this.session.RunOnMainThread = a => this.mainThreadQueue.Enqueue(a);
        this.socket.RunOnMainThread = a => this.mainThreadQueue.Enqueue(a);
        this.socket.SetHelloProvider(() => this.session.CurrentHello() ?? new { role = "dealer", token = "", dealer_name = "", dealer_world = "" });

        this.commandManager.AddHandler(MainCommand, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the dealer console. /pr config opens settings.",
            ShowInHelp = true,
        });

        this.framework.Update += this.OnFrameworkUpdate;
    }

    private bool socketStarted;

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Access control: never connect for non-whitelisted characters.
        if (this.session.EvaluateAuthorization())
        {
            if (!this.socketStarted)
            {
                this.socketStarted = true;
                this.socket.StartAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        this.log.Error(t.Exception, "socket startup failed");
                });
            }

            // Drain work produced on socket threads; UI/state mutations happen here only.
            while (this.mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    this.log.Error(e, "main-thread dispatch failed");
                }
            }

            this.dice.Tick();
        }

        this.ui.Draw();
    }

    private void OnCommand(string command, string arguments)
    {
        var arg = arguments.Trim();
        if (arg is "config" or "settings")
        {
            this.ui.OpenSettings();
            return;
        }

        if (!this.session.IsAuthorized)
        {
            // belt-and-braces: UI is locked too, but keep chat feedback explicit
            this.ui.OpenMain();
            return;
        }

        this.ui.OpenMain();
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.commandManager.RemoveHandler(MainCommand);
        this.ui.Dispose();
        this.dice.Dispose();
        this.session.Dispose();
        this.socket.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        this.config.Save();
    }
}
