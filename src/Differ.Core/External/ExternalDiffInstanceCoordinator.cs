using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Differ.Core.External;

public sealed class ExternalDiffInstanceCoordinator : IDisposable
{
    private const int MaximumPayloadLength = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.General);

    private readonly FileStream? _instanceLock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Task? _listenerTask;
    private bool _disposed;

    public ExternalDiffInstanceCoordinator(string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);

        var normalizedKey = NormalizeKey(instanceKey);
        _pipeName = $"Differ.ExternalDiff.{normalizedKey}";
        _instanceLock = TryAcquireInstanceLock(normalizedKey);
        IsPrimary = _instanceLock is not null;
    }

    public bool IsPrimary { get; }

    public event EventHandler<Exception>? ListenerError;

    public static string CreateCurrentDesktopSessionKey()
    {
        var identity = string.Join(
            "\u001f",
            Environment.UserDomainName,
            Environment.UserName,
            Process.GetCurrentProcess().SessionId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }

    public void StartListening(
        Func<ExternalDiffSession, CancellationToken, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);

        if (!IsPrimary)
        {
            throw new InvalidOperationException(
                "Only the primary instance can listen for comparison requests.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException(
                "The comparison request listener is already running.");
        }

        _listenerTask = Task.Run(
            () => ListenAsync(handler, _listenerCancellation.Token));
    }

    public async Task<bool> ForwardAsync(
        ExternalDiffSession session,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Timeout must be positive.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        if (payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException(
                "The comparison request payload is too large.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var forwardingToken = timeoutCancellation.Token;
        try
        {
            while (true)
            {
                forwardingToken.ThrowIfCancellationRequested();
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".",
                        _pipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await client.ConnectAsync(500, forwardingToken)
                        .ConfigureAwait(false);
                    await WritePayloadAsync(client, payload, forwardingToken)
                        .ConfigureAwait(false);

                    var acknowledgment = new byte[1];
                    await client.ReadExactlyAsync(
                            acknowledgment,
                            forwardingToken)
                        .ConfigureAwait(false);
                    return acknowledgment[0] == 1;
                }
                catch (TimeoutException)
                {
                    await Task.Delay(100, forwardingToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(100, forwardingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();

        var listenerTask = _listenerTask;
        if (listenerTask is null)
        {
            _listenerCancellation.Dispose();
        }
        else
        {
            // Observe the listener outcome and release the token source only
            // after the loop has exited, because the loop still reads the token
            // it was started with. Waiting here would deadlock: the handler
            // completes on the UI thread that normally calls Dispose.
            _ = listenerTask.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                _listenerCancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _instanceLock?.Dispose();
    }

    private async Task ListenAsync(
        Func<ExternalDiffSession, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);

                var session = await ReadSessionAsync(server, cancellationToken)
                    .ConfigureAwait(false);
                var acknowledgment = new byte[] { 1 };
                try
                {
                    await handler(session, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A single failed comparison must never stop the listener.
                    // Otherwise the primary instance keeps the instance lock
                    // while silently refusing every later forwarded file.
                    acknowledgment[0] = 0;
                    ListenerError?.Invoke(this, exception);
                }

                await server.WriteAsync(
                        acknowledgment,
                        cancellationToken)
                    .ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ListenerError?.Invoke(this, exception);

                // Pipe creation or handshake failures would otherwise spin this
                // loop, so back off briefly before accepting the next client.
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task<ExternalDiffSession> ReadSessionAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken)
            .ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (payloadLength is <= 0 or > MaximumPayloadLength)
        {
            throw new InvalidDataException(
                "The comparison request payload length is invalid.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<ExternalDiffSession>(
                payload,
                JsonOptions)
            ?? throw new InvalidDataException(
                "The comparison request payload is empty.");
    }

    private static async Task WritePayloadAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeKey(string instanceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(instanceKey));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }

    private static FileStream? TryAcquireInstanceLock(string normalizedKey)
    {
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "Differ",
            "InstanceLocks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{normalizedKey}.lock");

        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
