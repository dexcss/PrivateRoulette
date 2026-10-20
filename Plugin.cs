using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using Dalamud.Game.Command;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.ClientState.Objects.SubKinds;
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
    private readonly TradeService trade;
    private readonly PluginUi ui;
    private readonly ChatSender chatSender;

    /// <summary>Work items marshalled from socket threads onto the framework thread.</summary>
    private readonly ConcurrentQueue<Action> mainThreadQueue = new();

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework, IPluginLog log, IChatGui chatGui, IClientState clientState, IPlayerState playerState, IAddonLifecycle addonLifecycle, IGameGui gameGui, ITargetManager targetManager, IPartyList partyList)
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
        this.chatSender = new ChatSender(log);

        this.session.RunOnMainThread = a => this.mainThreadQueue.Enqueue(a);
        this.socket.RunOnMainThread = a => this.mainThreadQueue.Enqueue(a);

        // ICommandManager.ProcessCommand only dispatches plugin-registered commands;
        // it silently ignores /tell and /p. Send through the game's chat box instead.
        this.session.RunCommand = this.SendChatLine;
        this.session.SendChatText = this.SendChatLine;
        this.session.IsInParty = () => partyList.Length > 0;
        this.session.GetTargetPlayerName = () =>
        {
            try
            {
                if (targetManager.Target is IPlayerCharacter pc)
                {
                    var name = pc.Name.TextValue;
                    var world = pc.HomeWorld.Value.Name.ExtractText();
                    return string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
                }
            }
            catch { }
            return null;
        };
        this.trade = new TradeService(this.config, this.session, addonLifecycle, gameGui, chatGui, log);
        this.socket.SetHelloProvider(() => this.session.CurrentHello() ?? new { role = "dealer", token = "", dealer_name = "", dealer_world = "" });

        this.commandManager.AddHandler(MainCommand, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the dealer console. /pr config opens settings.",
            ShowInHelp = true,
        });

        this.framework.Update += this.OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Access control: never connect for non-whitelisted characters.
        if (this.session.EvaluateAuthorization())
        {
            this.socket.Tick();

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
            this.trade.Tick();
        }

        this.ui.Draw();
    }

    /// <summary>
    /// Sends a game chat line through the chat box (the same path the game uses
    /// when the dealer presses Enter). ICommandManager.ProcessCommand is only a
    /// fallback for plugin-registered commands - it ignores /tell and /p.
    /// </summary>
    private void SendChatLine(string message)
    {
        if (!this.chatSender.Send(message))
            this.commandManager.ProcessCommand(message);
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
        this.trade.Dispose();
        this.session.Dispose();
        this.socket.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        this.config.Save();
    }
}
