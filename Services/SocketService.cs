using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Tasks;

using Dalamud.Plugin.Services;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;

using SocketIOClient;
using SocketIO.Serializer.NewtonsoftJson;
using SocketIOClient.Transport;

namespace PrivateRoulette.Services;

/// <summary>
/// Socket.IO connection to wss://roulette.gamba.pro with typed dealer events.
/// All callbacks are marshalled onto the framework thread via <see cref="RunOnMainThread"/>.
/// </summary>
public sealed class SocketService : IDisposable, IAsyncDisposable
{
    private const string ServerUrl = "wss://roulette.gamba.pro";

    private readonly PluginConfig config;
    private readonly IPluginLog log;

    private SocketIOClient.SocketIO? socket;
    private bool connecting;
    private bool disposed;
    private int reconnectAttempt;

    /// <summary>Marshals socket-thread work to the framework thread. Set by the plugin.</summary>
    public Action<Action>? RunOnMainThread { get; set; }

    // ---- connection lifecycle ----
    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<AuthResult>? AuthResult;

    // ---- game events ----
    public event Action<GamesList>? GamesList;
    public event Action<GameCreated>? GameCreated;
    public event Action<GameState>? GameState;
    public event Action<PlayerCreated>? PlayerCreated;
    public event Action<SpinReady>? SpinReady;
    public event Action<SpinResult>? Spin;
    public event Action<RoundResult>? RoundResult;
    public event Action<TableHistory>? TableHistory;
    public event Action<ChatHistory>? ChatHistory;
    public event Action<ChatMessage>? ChatMessage;
    public event Action<ReadyUpdate>? ReadyUpdated;
    public event Action<AwayUpdate>? AwayUpdated;
    public event Action<PublicBets>? PublicBets;
    public event Action<string, string>? DealerError;   // (message, raw)
    public event Action<string, string>? DealerOk;      // (message, raw)
    public event Action<JObject>? RawEvent;             // catch-all for untyped events

    public bool IsConnected => this.socket?.Connected == true;

    public SocketService(PluginConfig config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    public async Task StartAsync()
    {
        await this.ConnectCoreAsync().ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync()
    {
        if (this.connecting || this.disposed) return;
        this.connecting = true;
        try
        {
            this.socket?.Dispose();
            var options = new SocketIOOptions
            {
                Transport = TransportProtocol.WebSocket,
                Reconnection = false, // we own the retry loop
            };

            var s = new SocketIOClient.SocketIO(ServerUrl, options);
            s.Serializer = new NewtonsoftJsonSerializer();

            s.OnConnected += (_, _) =>
            {
                this.log.Debug("socket connected");
                this.reconnectAttempt = 0;
                this.MainThread(() => this.Connected?.Invoke());
            };
            s.OnDisconnected += (_, reason) =>
            {
                this.log.Debug($"socket disconnected: {reason}");
                this.MainThread(() => this.Disconnected?.Invoke(reason));
                _ = this.ReconnectLaterAsync();
            };
            s.OnError += (_, e) => this.log.Debug($"socket error: {e}");

            RegisterIncoming(s);

            this.socket = s;
            await s.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Error(e, "connect failed");
            _ = this.ReconnectLaterAsync();
        }
        finally
        {
            this.connecting = false;
        }
    }

    private async Task ReconnectLaterAsync()
    {
        if (this.disposed) return;
        var attempt = ++this.reconnectAttempt;
        var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 30));
        this.log.Debug($"reconnect attempt {attempt} in {delay.TotalSeconds:0}s");
        await Task.Delay(delay).ConfigureAwait(false);
        await this.ConnectCoreAsync().ConfigureAwait(false);
    }

    private void RegisterIncoming(SocketIOClient.SocketIO s)
    {
        // ---- typed events ----
        s.On("dealer_auth_result", r => this.MainThreadRaise(this.Read<AuthResult>(r), this.AuthResult));
        s.On("dealer_games_list", r => this.MainThreadRaise(this.Read<GamesList>(r), this.GamesList));
        s.On("game_created", r => this.MainThreadRaise(this.Read<GameCreated>(r), this.GameCreated));
        s.On("game_deleted", r => this.MainThread(() => { /* surfaced via games list refresh */ }));
        s.On("dealer_game_state", r => this.MainThreadRaise(this.Read<GameState>(r), this.GameState));
        s.On("game_state", r => this.MainThreadRaise(this.Read<GameState>(r), this.GameState));
        s.On("player_created", r => this.MainThreadRaise(this.Read<PlayerCreated>(r), this.PlayerCreated));
        s.On("dealer_spin_ready", r => this.MainThreadRaise(this.Read<SpinReady>(r), this.SpinReady));
        s.On("spin", r => this.MainThreadRaise(this.Read<SpinResult>(r), this.Spin));
        s.On("round_result", r => this.MainThreadRaise(this.Read<RoundResult>(r), this.RoundResult));
        s.On("table_history", r => this.MainThreadRaise(this.Read<TableHistory>(r), this.TableHistory));
        s.On("chat_history", r => this.MainThreadRaise(this.Read<ChatHistory>(r), this.ChatHistory));
        s.On("chat_message", r => this.MainThreadRaise(this.Read<ChatMessage>(r), this.ChatMessage));
        s.On("player_ready_updated", r => this.MainThreadRaise(this.Read<ReadyUpdate>(r), this.ReadyUpdated));
        s.On("player_away_updated", r => this.MainThreadRaise(this.Read<AwayUpdate>(r), this.AwayUpdated));
        s.On("public_bets", r => this.MainThreadRaise(this.Read<PublicBets>(r), this.PublicBets));
        s.On("dealer_error", r =>
        {
            var jo = this.Read<JObject>(r);
            var msg = jo?["message"]?.ToString() ?? jo?.ToString(Newtonsoft.Json.Formatting.None) ?? "unknown error";
            this.MainThread(() => this.DealerError?.Invoke(msg, jo?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty));
        });
        s.On("dealer_ok", r =>
        {
            var jo = this.Read<JObject>(r);
            var msg = jo?["message"]?.ToString() ?? "ok";
            this.MainThread(() => this.DealerOk?.Invoke(msg, jo?.ToString(Newtonsoft.Json.Formatting.None) ?? string.Empty));
        });

        // ---- diagnostics + catch-all ----
        s.On("dealer_debug", r =>
        {
            if (this.config.VerboseWireLog)
                this.log.Debug($"[wire] dealer_debug {this.Read<JObject>(r)?.ToString(Newtonsoft.Json.Formatting.None)}");
        });
        s.OnAny((name, r) =>
        {
            if (this.config.VerboseWireLog)
                this.log.Debug($"[wire] Received event: {name}, data: {this.Read<JObject>(r)?.ToString(Newtonsoft.Json.Formatting.None)}");
            var jo = this.Read<JObject>(r);
            if (jo != null)
                this.MainThread(() => this.RawEvent?.Invoke(jo));
        });
    }

    private T? Read<T>(SocketIOResponse response) where T : class
    {
        try
        {
            return response.GetValue<T>(0);
        }
        catch (Exception e)
        {
            this.log.Error(e, $"failed to deserialize {typeof(T).Name}");
            return default;
        }
    }

    private void MainThreadRaise<T>(T? value, Action<T>? handler) where T : class
    {
        if (value == null || handler == null) return;
        var sink = this.RunOnMainThread;
        if (sink == null) handler(value);
        else sink(() => handler(value));
    }

    private void MainThread(Action a)
    {
        var sink = this.RunOnMainThread;
        if (sink == null) a();
        else sink(a);
    }

    // ---- emitting ----

    private IdentityBlock? identity;

    /// <summary>Sets the identity block merged into every dealer emit.</summary>
    public void SetIdentity(IdentityBlock id) => this.identity = id;

    public async Task EmitAsync(string eventName, object? payload = null)
    {
        var s = this.socket;
        if (s == null || !this.IsConnected)
        {
            this.log.Warning($"cannot emit {eventName}: not connected");
            return;
        }

        JObject jo = payload == null ? new JObject() : JObject.FromObject(payload);
        if (this.identity != null)
            foreach (var (k, v) in this.identity.ToJson())
                jo[k] = v;

        if (this.config.VerboseWireLog)
            this.log.Debug($"[wire] Emitting event: {eventName}, data: {jo.ToString(Newtonsoft.Json.Formatting.None)}");

        await s.EmitAsync(eventName, jo).ConfigureAwait(false);
    }

    // ---- convenience emits ----

    public Task AuthenticateAsync(IdentityBlock id)
    {
        this.SetIdentity(id);
        return this.EmitAsync("dealer_auth", id.ToJson());
    }

    public Task ListGamesAsync() => this.EmitAsync("dealer_list_games");

    public Task CreateGameAsync(string? theme, SpinMode mode, long maxInner, long maxOuter, long maxInnerVip, long maxOuterVip, long[]? chips)
    {
        var modeStr = mode switch
        {
            SpinMode.Control => "control",
            SpinMode.Random => "random",
            SpinMode.Manual => "manual",
            _ => "control",
        };
        return this.EmitAsync("dealer_create_game", new
        {
            theme,
            spin_mode = modeStr,
            max_bet_inner = maxInner,
            max_bet_outer = maxOuter,
            max_bet_inner_vip = maxInnerVip,
            max_bet_outer_vip = maxOuterVip,
            chip_denominations = chips,
        });
    }

    public Task JoinGameAsync(string gameId) => this.EmitAsync("dealer_join_game", new { game_id = gameId });

    public Task AddPlayerAsync(string name, string displayName, long bankroll, string gameId)
        => this.EmitAsync("dealer_add_player", new { name, display_name = displayName, bankroll, game_id = gameId });

    public Task AdjustBalanceAsync(string playerId, long delta, string gameId)
        => this.EmitAsync("dealer_adjust_balance", new { player_id = playerId, delta, game_id = gameId });

    public Task GetGameLedgerAsync(string gameId) => this.EmitAsync("dealer_get_game_ledger", new { game_id = gameId });

    public Task GetHistoricalLedgerAsync(string gameId) => this.EmitAsync("dealer_get_historical_ledger", new { game_id = gameId });

    public Task RequestSpinAsync(string gameId) => this.EmitAsync("dealer_request_spin", new { game_id = gameId });

    public Task SpinAsync(string gameId, int? forcedNumber, long? diceRoll)
        => this.EmitAsync("dealer_spin", new { number = forcedNumber, dice_roll = diceRoll, game_id = gameId });

    public Task ClearBetsAsync(string gameId) => this.EmitAsync("dealer_clearbets", new { game_id = gameId });

    public Task RebetAsync(string gameId) => this.EmitAsync("dealer_rebet", new { game_id = gameId });

    public Task RecordPayoutAsync(string playerId, long amount, string gameId)
        => this.EmitAsync("dealer_record_payout", new { player_id = playerId, amount, game_id = gameId });

    public Task SendChatAsync(string gameId, string text)
        => this.EmitAsync("chat_message", new { game_id = gameId, text });

    public async ValueTask DisposeAsync()
    {
        this.disposed = true;
        this.socket?.Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.socket?.Dispose();
    }
}

/// <summary>Per-character identity sent with every dealer emit.</summary>
public sealed class IdentityBlock
{
    [JsonProperty("character")] public string Character { get; init; } = string.Empty;
    [JsonProperty("homeworld")] public string Homeworld { get; init; } = string.Empty;
    [JsonProperty("char_id")] public string CharId { get; init; } = string.Empty;
    [JsonProperty("api_key")] public string ApiKey { get; init; } = string.Empty;

    public System.Collections.Generic.KeyValuePair<string, JToken>[] ToJson()
    {
        return new[]
        {
            new System.Collections.Generic.KeyValuePair<string, JToken>("character", this.Character),
            new System.Collections.Generic.KeyValuePair<string, JToken>("homeworld", this.Homeworld),
            new System.Collections.Generic.KeyValuePair<string, JToken>("char_id", this.CharId),
            new System.Collections.Generic.KeyValuePair<string, JToken>("api_key", this.ApiKey),
        };
    }
}
