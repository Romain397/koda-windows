using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Koda.Playback;

/// <summary>Writes immutable queue snapshots in order, keeping only the newest waiting state.</summary>
internal sealed class QueueStateStore(string path)
{
    internal sealed record State(PlayQueue.Saved Queue, double Position);

    private readonly object _gate = new();
    private State? _pending;
    private Task? _writer;

    public Task SaveAsync(State state)
    {
        lock (_gate)
        {
            _pending = state;
            return _writer ??= Task.Run(WritePending);
        }
    }

    public State? Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<State>(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WritePending()
    {
        while (true)
        {
            State state;
            lock (_gate)
            {
                if (_pending is not { } latest)
                {
                    _writer = null;
                    return;
                }

                state = latest;
                _pending = null;
            }

            try
            {
                var temporary = path + ".tmp";
                using (var stream = File.Create(temporary))
                {
                    JsonSerializer.Serialize(stream, state);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // An incomplete write leaves the previous queue intact. The next save tries again.
            }
        }
    }
}
