using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Koda.Interop;

/// <summary>Remembers the Windows mixer level of this process, separately from player gain.</summary>
internal sealed partial class WindowsMixerVolume : IDisposable
{
    private static readonly Guid RestoreContext = new("75A60DAE-9578-4417-A0E1-7F0904C263D6");
    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Endpoint, string Id), ObservedSession> _sessions = [];
    private readonly Thread _thread;
    private IDeviceEnumerator? _devices;
    private float _level;

    public WindowsMixerVolume(float? savedLevel)
    {
        _level = savedLevel is >= 0 and <= 1 ? savedLevel.Value : -1;
        _thread = new Thread(Watch) { IsBackground = true, Name = "Koda Windows mixer" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public event Action? Changed;

    public float? Level
    {
        get
        {
            var level = Volatile.Read(ref _level);
            return level >= 0 ? level : null;
        }
    }

    // Shutdown can arrive before the dispatcher saves the most recent mixer notification.
    public float? CaptureLevel()
    {
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_work.Writer.TryWrite(() =>
        {
            try
            {
                foreach (var (key, session) in _sessions)
                {
                    if (session.Volume.GetMasterVolume(out var actual) == 0 && actual != Level)
                    {
                        RememberLevel(key, actual);
                        break;
                    }
                }
            }
            finally
            {
                captured.TrySetResult();
            }
        }))
        {
            captured.Task.Wait(TimeSpan.FromMilliseconds(250));
        }

        return Level;
    }

    private void Watch()
    {
        const uint multithreaded = 0;
        if (CoInitializeEx(0, multithreaded) < 0)
        {
            _work.Writer.TryComplete();
            return;
        }

        var notifications = new DeviceNotifications(this);
        try
        {
            _devices = (IDeviceEnumerator)new DeviceEnumeratorClass();
            _devices.RegisterEndpointNotificationCallback(notifications);
            RunSafely(RefreshEndpoints);
            while (_work.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_work.Reader.TryRead(out var action)) RunSafely(action);
            }
        }
        catch (COMException)
        {
            // Playback remains usable when the Windows audio service is unavailable.
        }
        finally
        {
            _work.Writer.TryComplete();
            foreach (var id in _endpoints.Keys.ToArray()) RunSafely(() => RemoveEndpoint(id));
            RunSafely(() => _devices?.UnregisterEndpointNotificationCallback(notifications));
            _devices = null;
            CoUninitialize();
        }
    }

    private static void RunSafely(Action action)
    {
        try { action(); }
        catch (COMException) { }
    }

    private void RefreshEndpoints()
    {
        if (_devices is null || _devices.EnumAudioEndpoints(Render, ActiveDevices, out var devices) < 0
            || devices.GetCount(out var count) < 0) return;

        var active = new HashSet<string>(StringComparer.Ordinal);
        for (uint index = 0; index < count; index++)
        {
            if (devices.Item(index, out var device) < 0 || device.GetId(out var id) < 0) continue;
            active.Add(id);
            if (!_endpoints.ContainsKey(id)) AddEndpoint(id, device);
        }

        foreach (var id in _endpoints.Keys.Where(id => !active.Contains(id)).ToArray()) RemoveEndpoint(id);
    }

    private void AddEndpoint(string id, IDevice device)
    {
        var iid = typeof(ISessionManager).GUID;
        if (device.Activate(ref iid, InProcessServer, 0, out var instance) < 0 || instance is not ISessionManager manager) return;
        var callbacks = new NewSessions(this, id);
        if (manager.RegisterSessionNotification(callbacks) < 0) return;
        _endpoints.Add(id, new Endpoint(manager, callbacks));

        // GetCount also enables creation notifications for this endpoint.
        if (manager.GetSessionEnumerator(out var sessions) < 0 || sessions.GetCount(out var count) < 0)
        {
            RemoveEndpoint(id);
            return;
        }

        for (var index = 0; index < count; index++)
        {
            if (sessions.GetSession(index, out var session) == 0) Observe(id, session);
        }
    }

    private void RemoveEndpoint(string id)
    {
        if (_endpoints.Remove(id, out var endpoint))
            endpoint.Manager.UnregisterSessionNotification(endpoint.Callbacks);
        foreach (var key in _sessions.Keys.Where(key => key.Endpoint == id).ToArray()) Forget(key);
    }

    private void Observe(string endpoint, object candidate)
    {
        // A MediaPlayer session may span Koda and the Windows media host. A successful
        // GetProcessId still gives its creator, which must be the current process.
        if (!_endpoints.ContainsKey(endpoint) || candidate is not ISession session
            || session.GetProcessId(out var processId) < 0 || processId != (uint)Environment.ProcessId
            || session.GetState(out var state) < 0 || state == SessionExpired
            || session.GetSessionInstanceIdentifier(out var id) < 0 || candidate is not IVolume volume) return;

        var key = (endpoint, id);
        if (_sessions.ContainsKey(key)) return;
        var callbacks = new SessionEvents(this, key);
        if (session.RegisterAudioSessionNotification(callbacks) < 0) return;
        _sessions.Add(key, new ObservedSession(session, volume, callbacks));
        if (volume.GetMasterVolume(out var current) < 0) return;
        if (Level is { } saved) SetVolume(volume, saved, current);
        else RememberLevel(key, current);
    }

    private void RememberLevel((string Endpoint, string Id) source, float level)
    {
        if (!(level >= 0 && level <= 1) || Level == level || !_sessions.ContainsKey(source)) return;
        Volatile.Write(ref _level, level);
        Changed?.Invoke();
        // Crossfades and video playback may have another Koda session open.
        foreach (var (key, session) in _sessions)
        {
            if (key != source && session.Volume.GetMasterVolume(out var current) == 0)
                SetVolume(session.Volume, level, current);
        }
    }

    private static void SetVolume(IVolume volume, float level, float current)
    {
        if (level == current) return;
        var context = RestoreContext;
        volume.SetMasterVolume(level, ref context);
    }

    private void Forget((string Endpoint, string Id) key)
    {
        if (_sessions.Remove(key, out var observed))
            observed.Session.UnregisterAudioSessionNotification(observed.Callbacks);
    }

    public void Dispose()
    {
        _work.Writer.TryComplete();
        _thread.Join(TimeSpan.FromMilliseconds(250));
    }

    private sealed record Endpoint(ISessionManager Manager, INewSessions Callbacks);
    private sealed record ObservedSession(ISession Session, IVolume Volume, ISessionEvents Callbacks);

    private sealed class DeviceNotifications(WindowsMixerVolume owner) : IDeviceNotifications
    {
        private int Refresh()
        {
            owner._work.Writer.TryWrite(owner.RefreshEndpoints);
            return 0;
        }

        public int OnDeviceStateChanged(string id, uint state) => Refresh();
        public int OnDeviceAdded(string id) => Refresh();
        public int OnDeviceRemoved(string id) => Refresh();
        public int OnDefaultDeviceChanged(int flow, int role, string? id) => flow == Render ? Refresh() : 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    private sealed class NewSessions(WindowsMixerVolume owner, string endpoint) : INewSessions
    {
        public int OnSessionCreated(object session)
        {
            owner._work.Writer.TryWrite(() => owner.Observe(endpoint, session));
            return 0;
        }
    }

    private sealed class SessionEvents(WindowsMixerVolume owner, (string Endpoint, string Id) key) : ISessionEvents
    {
        public int OnSimpleVolumeChanged(float volume, int mute, nint context)
        {
            if (context == 0 || Marshal.PtrToStructure<Guid>(context) != RestoreContext)
                owner._work.Writer.TryWrite(() => owner.RememberLevel(key, volume));
            return 0;
        }

        public int OnStateChanged(int state)
        {
            if (state == SessionExpired) owner._work.Writer.TryWrite(() => owner.Forget(key));
            return 0;
        }

        public int OnSessionDisconnected(int reason)
        {
            owner._work.Writer.TryWrite(() => owner.Forget(key));
            return 0;
        }

        public int OnDisplayNameChanged(nint name, nint context) => 0;
        public int OnIconPathChanged(nint path, nint context) => 0;
        public int OnChannelVolumeChanged(uint count, nint levels, uint channel, nint context) => 0;
        public int OnGroupingParamChanged(nint grouping, nint context) => 0;
    }
}
