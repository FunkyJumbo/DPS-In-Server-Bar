using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DpsInServerBar.Combat;

namespace DpsInServerBar.Act;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// Keeps a WebSocket connection to OverlayPlugin's WSServer alive (with reconnect backoff)
/// and exposes the latest CombatData snapshot. Everything runs off the game thread.
/// </summary>
public sealed class ActClient : ICombatSource, IDisposable
{
    private static readonly byte[] SubscribeMessage =
        Encoding.UTF8.GetBytes("""{"call":"subscribe","events":["CombatData"]}""");

    private readonly IPluginLog log;
    private readonly Func<string?> playerNameProvider;
    private CancellationTokenSource? runCts;
    private Task? runTask;

    private volatile CombatSnapshot? latest;
    private volatile string? lastError;
    private int state = (int)ConnectionState.Disconnected;

    public ActClient(IPluginLog log, Func<string?> playerNameProvider)
    {
        this.log = log;
        this.playerNameProvider = playerNameProvider;
    }

    public Uri? Endpoint { get; private set; }
    public bool IsRunning => runCts is not null;
    public ConnectionState State => (ConnectionState)Volatile.Read(ref state);
    public string? LastError => lastError;
    public CombatSnapshot? Latest => latest;

    public string Name => "ACT";
    public bool IsReady => State == ConnectionState.Connected;

    public string StatusText => State switch
    {
        ConnectionState.Connected => "connected",
        ConnectionState.Connecting => "connecting...",
        _ => LastError is { } err ? $"disconnected ({err})" : "disconnected",
    };

    /// <summary>(Re)starts the connection loop against the given endpoint.</summary>
    public void Start(Uri endpoint)
    {
        Stop();
        Endpoint = endpoint;
        runCts = new CancellationTokenSource();
        var token = runCts.Token;
        runTask = Task.Run(() => RunAsync(endpoint, token), token);
    }

    public void Stop()
    {
        if (runCts is null)
            return;

        runCts.Cancel();
        try
        {
            runTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; nothing to do.
        }

        runCts.Dispose();
        runCts = null;
        runTask = null;
        SetState(ConnectionState.Disconnected);
    }

    public void ClearData() => latest = null;

    public void Dispose() => Stop();

    private async Task RunAsync(Uri endpoint, CancellationToken token)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                SetState(ConnectionState.Connecting);

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await ws.ConnectAsync(endpoint, connectCts.Token).ConfigureAwait(false);
                }

                await ws.SendAsync(SubscribeMessage, WebSocketMessageType.Text, true, token).ConfigureAwait(false);

                SetState(ConnectionState.Connected);
                lastError = null;
                delay = TimeSpan.FromSeconds(1);
                log.Information($"Connected to OverlayPlugin at {endpoint}");

                await ReceiveLoopAsync(ws, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Expected whenever ACT is closed: keep it at debug level to avoid spamming the log.
                if (lastError != ex.Message)
                    log.Debug($"OverlayPlugin connection failed: {ex.Message}");
                lastError = ex.Message;
            }

            SetState(ConnectionState.Disconnected);

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                lastError = "Connection closed by OverlayPlugin";
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            if (result.MessageType == WebSocketMessageType.Text)
                HandleMessage(message.GetBuffer().AsMemory(0, (int)message.Length));

            message.SetLength(0);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var snapshot = CombatDataParser.TryParse(doc.RootElement, playerNameProvider());
            if (snapshot is not null)
                latest = WithStableStart(snapshot, latest);
        }
        catch (JsonException ex)
        {
            log.Debug($"Ignoring malformed OverlayPlugin message: {ex.Message}");
        }
    }

    /// <summary>
    /// ACT only gives a duration, so the start is "received - duration", which jitters, and drifts once the
    /// encounter has ended while ACT keeps resending it. Keep the start of the same fight stable.
    /// </summary>
    private static CombatSnapshot WithStableStart(CombatSnapshot snapshot, CombatSnapshot? previous)
    {
        if (previous is null)
            return snapshot;

        var sameFrozenFight = !snapshot.IsActive && snapshot.Encounter.Duration == previous.Encounter.Duration;
        var sameRunningFight = Math.Abs((snapshot.StartUtc - previous.StartUtc).TotalSeconds) < 5;

        return sameFrozenFight || sameRunningFight ? snapshot with { StartUtc = previous.StartUtc } : snapshot;
    }

    private void SetState(ConnectionState s) => Volatile.Write(ref state, (int)s);
}
