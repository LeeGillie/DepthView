using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DepthView.Integrations.LightBurn.Control;

/// <summary>
/// Drives LightBurn over its UDP interface.
///
/// LightBurn listens for plain-text commands on 19840 and answers on 19841. Those are two
/// separate one-way sockets rather than a connection, which has three consequences this class
/// exists to handle:
///
///   Nothing correlates a reply to a request. There is no sequence number and no envelope, just
///   a datagram arriving. Request/response is therefore synthesised by allowing one command in
///   flight at a time and treating the next datagram as its answer - which is honest for a
///   protocol this shape, and is why <see cref="SendAsync"/> serialises.
///
///   Not every command replies. START and CLOSE may simply act. A caller that waits forever for
///   an answer that was never coming would hang the UI, so every wait has a timeout and a
///   timeout is a normal outcome rather than an error.
///
///   Delivery is not guaranteed. UDP will drop a datagram without telling anybody. A command
///   that appears to have been ignored may not have arrived, and nothing here can distinguish
///   the two - so no method reports success, only what came back.
///
/// Unsolicited datagrams still surface through <see cref="MessageReceived"/> whether or not
/// anybody is waiting, because a protocol that is only partly known will do things this class
/// does not expect and the useful thing is to see them.
/// </summary>
public sealed class LightBurnControl : IDisposable
{
    public const int DefaultSendPort = 19840;
    public const int DefaultListenPort = 19841;

    private readonly IPEndPoint _target;
    private readonly int _listenPort;

    private UdpClient? _rx;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<LightBurnMessage>? _pending;

    /// <summary>Every datagram received, in order, whether or not it answered a request.</summary>
    public event EventHandler<LightBurnMessage>? MessageReceived;

    /// <summary>Raised when the listener itself fails. The socket is closed by then.</summary>
    public event EventHandler<Exception>? ListenerFailed;

    /// <summary>Recent traffic, newest last, for a diagnostics pane. Bounded so a long session
    /// cannot grow without limit.</summary>
    public ConcurrentQueue<LightBurnMessage> History { get; } = new();

    public int HistoryLimit { get; set; } = 500;

    public bool Listening => _loop is { IsCompleted: false };

    public LightBurnControl(string host = "127.0.0.1",
                            int sendPort = DefaultSendPort,
                            int listenPort = DefaultListenPort)
    {
        _target = new IPEndPoint(IPAddress.Parse(host), sendPort);
        _listenPort = listenPort;
    }

    // ------------------------------------------------------------------ listening

    /// <summary>
    /// Start receiving. Safe to call twice; the second call does nothing.
    ///
    /// The socket takes ExclusiveAddressUse = false so that this can coexist with another tool
    /// already bound to the reply port. Two listeners both seeing LightBurn's answers is a far
    /// better failure than refusing to start because somebody left a script running.
    /// </summary>
    public void StartListening()
    {
        if (Listening) return;

        var rx = new UdpClient { ExclusiveAddressUse = false };
        rx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        rx.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));

        _rx = rx;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(rx, _cts.Token));
    }

    public void StopListening()
    {
        _cts?.Cancel();
        try { _rx?.Dispose(); } catch { /* closing a closed socket is not news */ }
        _rx = null;
        _loop = null;
    }

    private async Task ReceiveLoopAsync(UdpClient rx, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await rx.ReceiveAsync(ct).ConfigureAwait(false);

                var msg = new LightBurnMessage(
                    Encoding.UTF8.GetString(result.Buffer).TrimEnd('\0', '\r', '\n'),
                    DateTimeOffset.Now,
                    result.RemoteEndPoint.ToString());

                History.Enqueue(msg);
                while (History.Count > HistoryLimit) History.TryDequeue(out _);

                // Hand it to a waiting request first, then to everyone. A reply is still worth
                // seeing in the log even when it satisfied a call.
                TaskCompletionSource<LightBurnMessage>? waiter;
                lock (_gate) { waiter = _pending; _pending = null; }
                waiter?.TrySetResult(msg);

                MessageReceived?.Invoke(this, msg);
            }
        }
        catch (OperationCanceledException) { /* asked to stop */ }
        catch (ObjectDisposedException) { /* socket closed under us, same thing */ }
        catch (Exception ex)
        {
            ListenerFailed?.Invoke(this, ex);
        }
    }

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Send a command and give up on a reply after <paramref name="timeout"/>.
    ///
    /// Returns null when nothing came back, which is not necessarily a failure: several of
    /// these commands are not known to answer at all.
    /// </summary>
    public async Task<LightBurnMessage?> SendAsync(string command,
                                                   TimeSpan? timeout = null,
                                                   CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty.", nameof(command));

        timeout ??= TimeSpan.FromSeconds(2);

        // Without a listener there is nowhere for an answer to arrive, and waiting for one
        // would burn the timeout on every call for no reason.
        bool wait = Listening;

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TaskCompletionSource<LightBurnMessage>? tcs = null;
            if (wait)
            {
                tcs = new TaskCompletionSource<LightBurnMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _pending = tcs;
            }

            var bytes = Encoding.UTF8.GetBytes(command);
            using (var tx = new UdpClient())
                await tx.SendAsync(bytes, bytes.Length, _target).ConfigureAwait(false);

            if (tcs is null) return null;

            using var timer = new CancellationTokenSource(timeout.Value);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);

            var done = await Task.WhenAny(
                tcs.Task, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

            if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);

            lock (_gate) { if (ReferenceEquals(_pending, tcs)) _pending = null; }
            return null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Send anything at all, for commands not in <see cref="LightBurnCommands"/>.
    ///
    /// Present because the command set is community knowledge over an undocumented protocol.
    /// A typed method for each known command is the convenient path; this is the one that stays
    /// useful when the list turns out to be incomplete.
    /// </summary>
    public Task<LightBurnMessage?> SendRawAsync(string command,
                                                TimeSpan? timeout = null,
                                                CancellationToken ct = default)
        => SendAsync(command, timeout, ct);

    // ------------------------------------------------------------------ typed commands

    public Task<LightBurnMessage?> PingAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAsync(LightBurnCommands.Ping, timeout, ct);

    public Task<LightBurnMessage?> StatusAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAsync(LightBurnCommands.Status, timeout, ct);

    /// <summary>Open a project. The path is sent as given - LightBurn resolves it on its own
    /// machine, which is not necessarily this one.</summary>
    public Task<LightBurnMessage?> LoadFileAsync(string path, TimeSpan? timeout = null,
                                                 CancellationToken ct = default)
        => SendAsync(LightBurnCommands.LoadFile + path, timeout, ct);

    /// <summary>Open a project, discarding unsaved changes without prompting. Destructive to
    /// whatever the operator had open and unsaved.</summary>
    public Task<LightBurnMessage?> ForceLoadFileAsync(string path, TimeSpan? timeout = null,
                                                      CancellationToken ct = default)
        => SendAsync(LightBurnCommands.ForceLoad + path, timeout, ct);

    /// <summary>Start the loaded job. Fires a laser: never call this speculatively, and never
    /// as part of a connectivity check.</summary>
    public Task<LightBurnMessage?> StartJobAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAsync(LightBurnCommands.Start, timeout, ct);

    public Task<LightBurnMessage?> CloseAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAsync(LightBurnCommands.Close, timeout, ct);

    /// <summary>Close without prompting to save. Discards the operator's unsaved work.</summary>
    public Task<LightBurnMessage?> ForceCloseAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => SendAsync(LightBurnCommands.ForceClose, timeout, ct);

    /// <summary>
    /// Whether LightBurn answers a PING. A false is weak evidence - the datagram may simply
    /// have been dropped - so callers should say "no reply" rather than "not running".
    /// </summary>
    public async Task<bool> IsRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        => await PingAsync(timeout ?? TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false) is not null;

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
        _oneAtATime.Dispose();
    }
}
