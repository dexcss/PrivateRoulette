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
///
/// Connection management is driven by <see cref="Tick"/> on the framework
/// thread — no background watchdog tasks. All sends are serialized through a
/// single gate because ClientWebSocket permits only one outstanding send.
/// </summary>
public sealed class SocketService : IDisposable, IAsyncDisposable
{
    private readonly PluginConfig config;
    private readonly IPluginLog log;

    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private bool connecting;
    private bool disposed;
    private int reconnectAttempt;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset lastServerActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset pingSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastHeartbeatLog = DateTimeOffset.MinValue;
    private Func<object>? helloProvider;

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
    public event Action<DealerStats>? DealerStats;
    public event Action<string, string>? DealerError;  // (message, raw)
    public event Action<string, string>? DealerOk;     // (message, raw)
    public event Action<string>? GameClosed;

    public bool IsConnected => this.ws?.State == WebSocketState.Open;

    public SocketService(PluginConfig config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    /// <summary>Called every framework tick. Owns connect retries and the heartbeat.</summary>
    public void Tick()
    {
        if (this.disposed) return;
        var now = DateTimeOffset.UtcNow;

        if (this.ws?.State != WebSocketState.Open)
        {
            // reconnect with backoff
            var delay = Math.Min(2 * Math.Max(1, this.reconnectAttempt), 30);
            if (!this.connecting && (now - this.lastAttempt).TotalSeconds >= delay)
                _ = this.ConnectAsync();
            return;
        }

        var silent = (now - this.lastServerActivity).TotalSeconds;

        // periodic heartbeat trace (verbose log only) so failures are diagnosable
        if (this.config.VerboseWireLog && (now - this.lastHeartbeatLog).TotalSeconds > 10)
        {
            this.lastHeartbeatLog = now;
            this.log.Debug($"[hb] rx={silent:0}s state={this.ws?.State} pingAge={(now - this.pingSentAt).TotalSeconds:0}s");
        }

        // keep the connection warm: ping after 5s of silence, drop if nothing
        // answers by 20s (hibernating edges and proxies kill idle sockets)
        if (silent > 20)
        {
            this.log.Warning($"backend silent for {silent:0}s — reconnecting");
            this.DropConnection("watchdog: backend silent");
            return;
        }

        if (silent > 5 && (now - this.pingSentAt).TotalSeconds > 4)
        {
            this.pingSentAt = now;
            if (this.config.VerboseWireLog)
                this.log.Debug("[wire] → ping (heartbeat)");
            _ = this.SendRawAsync("{\"t\":\"ping\",\"d\":{}}");
        }
    }

    public Task StartAsync()
    {
        if (this.disposed || this.connecting || this.ws?.State == WebSocketState.Open)
            return Task.CompletedTask;

        return this.ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (this.connecting || this.disposed) return;
        this.connecting = true;
        this.lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            this.cts?.Cancel();
            try { this.ws?.Dispose(); } catch { }
            this.ws = null;

            var localCts = new CancellationTokenSource();
            this.cts = localCts;

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(localCts.Token, timeout.Token);

            await socket.ConnectAsync(new Uri(this.config.ApiUrl), linked.Token).ConfigureAwait(false);

            this.ws = socket;
            this.reconnectAttempt = 0;
            this.lastServerActivity = DateTimeOffset.UtcNow;
            this.pingSentAt = DateTimeOffset.MinValue;
            this.log.Debug("backend connected");

            _ = this.ReceiveLoop(socket, localCts.Token);

            this.MainThread(() =>
            {
                this.Connected?.Invoke();
                if (this.helloProvider != null)
                    _ = this.SendAsync("hello", this.helloProvider());
            });
        }
        catch (Exception e)
        {
            this.reconnectAttempt++;
            this.log.Warning($"connect failed ({this.reconnectAttempt}): {e.Message}");
            this.ws = null;
        }
        finally
        {
            this.connecting = false;
        }
    }

    private void DropConnection(string reason)
    {
        this.log.Info($"disconnecting: {reason}");
        try { this.cts?.Cancel(); } catch { }
        try { this.ws?.Dispose(); } catch { }
        this.ws = null;
        this.pingSentAt = DateTimeOffset.MinValue;
        this.MainThread(() => this.Disconnected?.Invoke(reason));
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
                        this.log.Info($"server closed connection: {result.CloseStatus} {result.CloseStatusDescription}");
                        this.DropConnection("server closed");
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                this.lastServerActivity = DateTimeOffset.UtcNow;
                this.pingSentAt = DateTimeOffset.MinValue;

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
                this.Dispatch(t, d);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on drop/dispose
        }
        catch (Exception e)
        {
            this.log.Warning($"receive loop ended: {e.Message}");
            this.DropConnection("receive error");
        }
    }

    private void Dispatch(string t, JObject d)
    {
        if (this.config.VerboseWireLog)
            this.log.Debug($"[wire] ← {t} {d.ToString(Formatting.None)}");

        switch (t)
        {
            case "pong":
                break;
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
            case "dealer_stats":
                this.MainThreadRaise(this.Read<DealerStats>(d), this.DealerStats);
                break;
            case "game_closed":
                this.MainThread(() => this.GameClosed?.Invoke(d["game_id"]?.ToString() ?? ""));
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

    private async Task SendRawAsync(string json)
    {
        await this.sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var socket = this.ws;
            if (socket?.State != WebSocketState.Open)
                throw new InvalidOperationException("not connected");

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this.log.Warning($"send failed: {e.Message}");
            this.DropConnection("send failed");
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public async Task SendAsync(string eventName, object? payload = null)
    {
        var env = new JObject { ["t"] = eventName, ["d"] = payload == null ? new JObject() : JObject.FromObject(payload) };
        if (this.config.VerboseWireLog)
            this.log.Debug($"[wire] → {eventName} {env["d"]!.ToString(Formatting.None)}");

        await this.SendRawAsync(env.ToString(Formatting.None)).ConfigureAwait(false);
    }

    // ---- convenience emits (dealer) ----

    public Task ListGamesAsync() => this.SendAsync("list_games");

    public Task CreateGameAsync(string? theme, SpinMode mode, long maxInner, long maxOuter, long maxInnerVip, long maxOuterVip, long maxTotal, long maxTotalVip, long[]? chips, string dealerName, string dealerWorld)
        => this.SendAsync("create_game", new
        {
            theme,
            spin_mode = ModeStr(mode),
            max_bet_inner = maxInner,
            max_bet_outer = maxOuter,
            max_bet_inner_vip = maxInnerVip,
            max_bet_outer_vip = maxOuterVip,
            max_bet_total = maxTotal,
            max_bet_total_vip = maxTotalVip,
            chip_denominations = chips,
            dealer_name = dealerName,
            dealer_world = dealerWorld,
        });

    public Task JoinGameAsync(string gameId) => this.SendAsync("join_game", new { game_id = gameId });

    public Task AddPlayerAsync(string name, string displayName, long bankroll, string gameId, bool isVip = false, bool isFreeplay = false)
        => this.SendAsync("add_player", new { name, display_name = displayName, bankroll, game_id = gameId, is_vip = isVip, is_freeplay = isFreeplay });

    public Task AdjustBalanceAsync(string playerId, long delta, string gameId, string reason = "Dealer adjustment")
        => this.SendAsync("adjust_balance", new { player_id = playerId, delta, reason, game_id = gameId });

    public Task SetPlayerVipAsync(string playerId, bool isVip, string gameId)
        => this.SendAsync("set_player_vip", new { player_id = playerId, is_vip = isVip, game_id = gameId });

    public Task GetLedgerAsync(string gameId) => this.SendAsync("get_ledger", new { game_id = gameId });

    public Task GetStatsAsync() => this.SendAsync("get_stats");

    public Task CashoutPlayerAsync(string playerId, string gameId)
        => this.SendAsync("cashout_player", new { player_id = playerId, game_id = gameId });

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
        try { this.cts?.Cancel(); } catch { }
        try { this.ws?.Dispose(); } catch { }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        this.disposed = true;
        try { this.cts?.Cancel(); } catch { }
        try { this.ws?.Dispose(); } catch { }
    }
}
