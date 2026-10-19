using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin.Services;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;

namespace PrivateRoulette.Services;

/// <summary>
/// WebSocket connection to the Private Roulette backend (Cloudflare Worker +
/// Durable Object). Plain JSON envelope protocol: {"t":"<event>","d":{...}}.
/// No third-party services, no Socket.IO. All callbacks are marshalled onto the
/// framework thread via <see cref="RunOnMainThread"/>.
/// </summary>
public sealed class SocketService : IDisposable, IAsyncDisposable
{
    private readonly PluginConfig config;
    private readonly IPluginLog log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private bool connecting;
    private bool disposed;
    private int reconnectAttempt;
    private Func<object>? helloProvider;

    // ---- watchdog: detect zombie sockets the OS still considers "open" ----
    private DateTimeOffset lastServerActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset lastPongUtc = DateTimeOffset.MinValue;
    private DateTimeOffset pingSentAt = DateTimeOffset.MinValue;
    private Task? watchdogTask;

    /// <summary>Marshals socket-thread work to the framework thread. Set by the plugin.</summary>
    public Action<Action>? RunOnMainThread { get; set; }

    /// <summary>Provides the hello payload (identity + token) when the socket opens.</summary>
    public void SetHelloProvider(Func<object> provider) => this.helloProvider = provider;

    // ---- connection lifecycle ----
    public event Action? Connected;
    public event Action? AuthOk;                       // hello_ok received
    public event Action<string>? Disconnected;

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
    public event Action<HistoricalLedger>? Ledger;
    public event Action<string, string>? DealerError;  // (message, raw)
    public event Action<string, string>? DealerOk;     // (message, raw)
    public event Action<string>? GameClosed;

    public bool IsConnected => this.ws?.State == WebSocketState.Open;

    public SocketService(PluginConfig config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    public async Task StartAsync()
    {
        if (this.connecting || this.disposed) return;
        this.connecting = true;
        try
        {
            this.cts?.Cancel();
            this.cts = new CancellationTokenSource();
            this.ws?.Dispose();

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(new Uri(this.config.ApiUrl), this.cts.Token).ConfigureAwait(false);
            this.ws = socket;

            this.reconnectAttempt = 0;
            this.lastServerActivity = DateTimeOffset.UtcNow;
            this.lastPongUtc = DateTimeOffset.MinValue;
            this.pingSentAt = DateTimeOffset.MinValue;
            this.log.Debug("backend connected");
            this.watchdogTask = Task.Run(() => this.WatchdogLoop(this.cts!.Token));
            this.MainThread(() =>
            {
                this.Connected?.Invoke();
                if (this.helloProvider != null)
                    _ = this.SendAsync("hello", this.helloProvider());
            });

            _ = this.ReceiveLoop(socket, this.cts.Token);
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

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                        this.OnDisconnected("server closed connection");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(message.ToArray());
                JObject env;
                try
                {
                    env = JObject.Parse(text);
                }
                catch
                {
                    continue;
                }

                var t = env["t"]?.ToString() ?? "";
                var d = env["d"] as JObject ?? new JObject();
                this.lastServerActivity = DateTimeOffset.UtcNow;
                this.Dispatch(t, d);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            this.log.Debug(e, "receive loop ended");
            this.OnDisconnected(e.Message);
        }
    }

    private void OnDisconnected(string reason)
    {
        this.log.Debug($"disconnected: {reason}");
        this.MainThread(() =>
        {
            this.Disconnected?.Invoke(reason);
        });
        if (!this.disposed)
            _ = this.ReconnectLaterAsync();
    }

    /// <summary>
    /// Application-level heartbeat. Protocol keepalives can't detect a socket
    /// that the remote side silently orphaned (e.g. after a backend deploy),
    /// so we ping and expect pong — silence forces a reconnect.
    /// </summary>
    private async Task WatchdogLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !this.disposed)
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                var socket = this.ws;
                if (socket?.State != WebSocketState.Open) continue;

                var sinceActivity = DateTimeOffset.UtcNow - this.lastServerActivity;
                var sincePing = DateTimeOffset.UtcNow - this.pingSentAt;

                var pingUnanswered = this.pingSentAt > DateTimeOffset.MinValue
                                     && sincePing.TotalSeconds > 12
                                     && this.lastPongUtc < this.pingSentAt;

                if (pingUnanswered)
                {
                    this.log.Warning($"backend silent for {sinceActivity.TotalSeconds:0}s (ping unanswered {sincePing.TotalSeconds:0}s) — forcing reconnect");
                    this.pingSentAt = DateTimeOffset.MinValue;
                    try { socket.Dispose(); } catch { }
                    this.OnDisconnected("watchdog: backend silent");
                    return;
                }

                if (sinceActivity.TotalSeconds > 15)
                {
                    this.pingSentAt = DateTimeOffset.UtcNow;
                    try
                    {
                        var ping = Encoding.UTF8.GetBytes("{\"t\":\"ping\",\"d\":{}}");
                        await socket.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        this.OnDisconnected("watchdog: ping failed");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task ReconnectLaterAsync()
    {
        if (this.disposed) return;
        var attempt = ++this.reconnectAttempt;
        var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 30));
        this.log.Debug($"reconnect attempt {attempt} in {delay.TotalSeconds:0}s");
        await Task.Delay(delay).ConfigureAwait(false);
        await this.StartAsync().ConfigureAwait(false);
    }

    private void Dispatch(string t, JObject d)
    {
        if (this.config.VerboseWireLog)
            this.log.Debug($"[wire] ← {t} {d.ToString(Formatting.None)}");

        switch (t)
        {
            case "hello_ok":
                if ((d["role"]?.ToString() ?? "") == "dealer")
                    this.MainThread(() => this.AuthOk?.Invoke());
                break;
            case "games_list":
                this.MainThreadRaise(this.Read<GamesList>(d), this.GamesList);
                break;
            case "game_created":
                this.MainThreadRaise(this.Read<GameCreated>(d), this.GameCreated);
                break;
            case "game_state":
                this.MainThreadRaise(this.Read<GameState>(d), this.GameState);
                break;
            case "player_created":
                this.MainThreadRaise(this.Read<PlayerCreated>(d), this.PlayerCreated);
                break;
            case "dealer_spin_ready":
                this.MainThreadRaise(this.Read<SpinReady>(d), this.SpinReady);
                break;
            case "spin":
                this.MainThreadRaise(this.Read<SpinResult>(d), this.Spin);
                break;
            case "round_result":
                this.MainThreadRaise(this.Read<RoundResult>(d), this.RoundResult);
                break;
            case "table_history":
                this.MainThreadRaise(this.Read<TableHistory>(d), this.TableHistory);
                break;
            case "chat_history":
                this.MainThreadRaise(this.Read<ChatHistory>(d), this.ChatHistory);
                break;
            case "chat_message":
                this.MainThreadRaise(this.Read<ChatMessage>(d), this.ChatMessage);
                break;
            case "player_ready_updated":
                this.MainThreadRaise(this.Read<ReadyUpdate>(d), this.ReadyUpdated);
                break;
            case "player_away_updated":
                this.MainThreadRaise(this.Read<AwayUpdate>(d), this.AwayUpdated);
                break;
            case "public_bets":
                this.MainThreadRaise(this.Read<PublicBets>(d), this.PublicBets);
                break;
            case "ledger":
                this.MainThreadRaise(this.Read<HistoricalLedger>(d), this.Ledger);
                break;
            case "game_closed":
                this.MainThread(() => this.GameClosed?.Invoke(d["game_id"]?.ToString() ?? ""));
                break;
            case "pong":
                this.lastPongUtc = DateTimeOffset.UtcNow;
                break;
            case "dealer_error":
                var errMsg = d["message"]?.ToString() ?? "error";
                this.MainThread(() => this.DealerError?.Invoke(errMsg, d.ToString(Formatting.None)));
                break;
            case "dealer_ok":
                var okMsg = d["message"]?.ToString() ?? "ok";
                this.MainThread(() => this.DealerOk?.Invoke(okMsg, d.ToString(Formatting.None)));
                break;
        }
    }

    private T? Read<T>(JObject d) where T : class
    {
        try
        {
            return d.ToObject<T>();
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
        this.MainThread(() => handler(value));
    }

    private void MainThread(Action a)
    {
        var sink = this.RunOnMainThread;
        if (sink == null) a();
        else sink(a);
    }

    // ---- sending ----

    public async Task SendAsync(string eventName, object? payload = null)
    {
        var socket = this.ws;
        if (socket?.State != WebSocketState.Open)
        {
            this.log.Warning($"cannot send {eventName}: not connected");
            return;
        }

        var env = new JObject { ["t"] = eventName, ["d"] = payload == null ? new JObject() : JObject.FromObject(payload) };
        if (this.config.VerboseWireLog)
            this.log.Debug($"[wire] → {eventName} {env["d"]!.ToString(Formatting.None)}");

        var bytes = Encoding.UTF8.GetBytes(env.ToString(Formatting.None));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
    }

    // ---- convenience emits (dealer) ----

    public Task ListGamesAsync() => this.SendAsync("list_games");

    public Task CreateGameAsync(string? theme, SpinMode mode, long maxInner, long maxOuter, long maxInnerVip, long maxOuterVip, long[]? chips, string dealerName, string dealerWorld)
        => this.SendAsync("create_game", new
        {
            theme,
            spin_mode = ModeStr(mode),
            max_bet_inner = maxInner,
            max_bet_outer = maxOuter,
            max_bet_inner_vip = maxInnerVip,
            max_bet_outer_vip = maxOuterVip,
            chip_denominations = chips,
            dealer_name = dealerName,
            dealer_world = dealerWorld,
        });

    public Task JoinGameAsync(string gameId) => this.SendAsync("join_game", new { game_id = gameId });

    public Task AddPlayerAsync(string name, string displayName, long bankroll, string gameId)
        => this.SendAsync("add_player", new { name, display_name = displayName, bankroll, game_id = gameId });

    public Task AdjustBalanceAsync(string playerId, long delta, string gameId)
        => this.SendAsync("adjust_balance", new { player_id = playerId, delta, game_id = gameId });

    public Task GetLedgerAsync(string gameId) => this.SendAsync("get_ledger", new { game_id = gameId });

    public Task RequestSpinAsync(string gameId) => this.SendAsync("request_spin", new { game_id = gameId });

    public Task SpinAsync(string gameId, int? forcedNumber, long? diceRoll)
        => this.SendAsync("spin", new { number = forcedNumber, dice_roll = diceRoll, game_id = gameId });

    public Task ClearBetsAsync(string gameId) => this.SendAsync("clear_bets", new { game_id = gameId });

    public Task RebetAsync(string gameId) => this.SendAsync("rebet", new { game_id = gameId });

    public Task RecordPayoutAsync(string playerId, long amount, string gameId)
        => this.SendAsync("record_payout", new { player_id = playerId, amount, game_id = gameId });

    public Task SendChatAsync(string gameId, string text)
        => this.SendAsync("send_chat", new { game_id = gameId, text });

    public Task EndGameAsync(string gameId) => this.SendAsync("end_game", new { game_id = gameId });

    public Task DeleteGameAsync(string gameId) => this.SendAsync("delete_game", new { game_id = gameId });

    private static string ModeStr(SpinMode mode) => mode switch
    {
        SpinMode.Control => "control",
        SpinMode.Random => "random",
        SpinMode.Manual => "manual",
        _ => "control",
    };

    public async ValueTask DisposeAsync()
    {
        this.disposed = true;
        this.cts?.Cancel();
        try { this.ws?.Dispose(); } catch { }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.cts?.Cancel();
        try { this.ws?.Dispose(); } catch { }
    }
}
