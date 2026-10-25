using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Orbit.Services;

/// <summary>Minimal Discord Rich Presence client over Discord's local IPC pipe.</summary>
public sealed class DiscordRpc : IDisposable
{
    private readonly string _clientId;
    private NamedPipeClientStream? _pipe;
    private readonly object _gate = new();

    public DiscordRpc(string clientId) => _clientId = clientId;

    public bool IsConnected => _pipe is { IsConnected: true };

    private bool EnsureConnected()
    {
        if (IsConnected) return true;
        _pipe?.Dispose();
        _pipe = null;

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(300);
                _pipe = pipe;
                Write(0, new { v = 1, client_id = _clientId });
                ReadFrame(); // READY
                StartDrain();
                return true;
            }
            catch
            {
                pipe.Dispose();
                _pipe = null;
            }
        }
        return false;
    }

    private void Write(int opcode, object payload)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var frame = new byte[8 + json.Length];
        BitConverter.GetBytes(opcode).CopyTo(frame, 0);
        BitConverter.GetBytes(json.Length).CopyTo(frame, 4);
        json.CopyTo(frame, 8);
        _pipe!.Write(frame, 0, frame.Length);
        _pipe.Flush();
    }

    private void ReadFrame()
    {
        var header = new byte[8];
        ReadExactly(header);
        var length = BitConverter.ToInt32(header, 4);
        if (length > 0 && length < 1_000_000) ReadExactly(new byte[length]);
    }

    private void ReadExactly(byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _pipe!.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new IOException("Discord closed the connection.");
            read += n;
        }
    }

    /// <summary>Discord answers every command; read and discard so the pipe never fills up.</summary>
    private void StartDrain()
    {
        var pipe = _pipe;
        Task.Run(() =>
        {
            try
            {
                while (pipe is { IsConnected: true } && ReferenceEquals(pipe, _pipe)) ReadFrame();
            }
            catch { /* disconnected */ }
        });
    }

    /// <summary>Sets the activity; pass null to clear it.</summary>
    public void SetActivity(object? activity)
    {
        lock (_gate)
        {
            try
            {
                if (!EnsureConnected()) return;
                Write(1, new
                {
                    cmd = "SET_ACTIVITY",
                    args = new { pid = Environment.ProcessId, activity },
                    nonce = Guid.NewGuid().ToString(),
                });
            }
            catch
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { if (IsConnected) SetActivity(null); } catch { /* ignore */ }
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
