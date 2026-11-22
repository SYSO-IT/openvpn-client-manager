using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace OpenVpnClientManager.Services;

public sealed class InteractiveServiceException : Exception
{
    public uint ErrorCode { get; }
    public string Function { get; }

    public InteractiveServiceException(uint code, string function, string message)
        : base(message)
    {
        ErrorCode = code;
        Function = function;
    }
}

/// <summary>
/// A single openvpn.exe process started through the OpenVPN Interactive Service.
///
/// Protocol (src/openvpnserv/interactive.c):
///   client -> service : "workdir\0options\0stdin\0" as UTF-16LE on a message-mode pipe
///   service -> client : "0x%08x\n0x%08x\nProcess ID"   on success (2nd value = PID)
///                       "0x%08x\nfunction\nmessage"    on error
/// The service terminates openvpn.exe as soon as the client closes the pipe, so this
/// object must stay alive for the duration of the connection.
/// </summary>
public sealed class InteractiveServiceSession : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public int ProcessId { get; private set; }

    /// <summary>Raised when the service sends a message after start-up (usually an error).</summary>
    public event Action<string>? MessageReceived;

    /// <summary>Raised when the service closes the pipe, i.e. openvpn.exe has exited.</summary>
    public event Action? Closed;

    private InteractiveServiceSession(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
    }

    public static async Task<InteractiveServiceSession> StartAsync(string workingDirectory, string options, string stdin, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", OpenVpnEnvironment.ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;

            var message = Encoding.Unicode.GetBytes(workingDirectory + "\0" + options + "\0" + stdin + "\0");
            await pipe.WriteAsync(message, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);

            var session = new InteractiveServiceSession(pipe);
            var response = await session.ReadMessageAsync(ct).ConfigureAwait(false)
                           ?? throw new InteractiveServiceException(0xFFFFFFFF, "pipe", "De Interactive Service sloot de verbinding zonder antwoord.");

            var (code, second, third) = ParseResponse(response);
            if (code == 0 && third.StartsWith("Process ID", StringComparison.OrdinalIgnoreCase))
            {
                session.ProcessId = ParseHex(second);
                _ = session.ReadLoopAsync();
                return session;
            }

            pipe.Dispose();
            throw new InteractiveServiceException(code, second, third.Trim());
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static (uint code, string second, string third) ParseResponse(string response)
    {
        var parts = response.Split('\n', 3);
        uint code = parts.Length > 0 ? (uint)ParseHex(parts[0]) : 0xFFFFFFFF;
        return (code, parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 ? parts[2] : string.Empty);
    }

    private static int ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        return unchecked((int)uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        do
        {
            int n = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return ms.Length == 0 ? null : Decode(ms);
            }
            ms.Write(buffer, 0, n);
        }
        while (!_pipe.IsMessageComplete);
        return Decode(ms);

        static string Decode(MemoryStream m) => Encoding.Unicode.GetString(m.GetBuffer(), 0, (int)m.Length).TrimEnd('\0');
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(_cts.Token).ConfigureAwait(false);
                if (msg is null)
                {
                    break;
                }
                var (code, second, third) = ParseResponse(msg);
                MessageReceived?.Invoke(code == 0 ? third : $"{second}: {third} (0x{code:X8})");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // pipe closed
        }
        catch (Exception ex)
        {
            AppLog.Warn("Interactive service read loop: " + ex.Message);
        }
        finally
        {
            Closed?.Invoke();
        }
    }

    /// <summary>Closing the pipe makes the service terminate openvpn.exe (after a 2 s grace period).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try
        {
            _pipe.Dispose();
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
    }
}
