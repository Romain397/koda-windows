using System;
using System.Runtime.InteropServices;

namespace Koda.Interop;

internal sealed partial class WindowsMixerVolume
{
    private const int Render = 0;
    private const uint ActiveDevices = 1;
    private const uint InProcessServer = 1;
    private const int SessionExpired = 2;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint apartment);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IDeviceNotifications callbacks);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IDeviceNotifications callbacks);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint mode, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceNotifications
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManager
    {
        // IAudioSessionManager slots precede those of IAudioSessionManager2.
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IVolume volume);
        [PreserveSig] int GetSessionEnumerator(out ISessionEnumerable sessions);
        [PreserveSig] int RegisterSessionNotification(INewSessions callbacks);
        [PreserveSig] int UnregisterSessionNotification(INewSessions callbacks);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumerable
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISession
    {
        // IAudioSessionControl slots precede those of IAudioSessionControl2.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(ISessionEvents callbacks);
        [PreserveSig] int UnregisterAudioSessionNotification(ISessionEvents callbacks);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
    }

    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INewSessions
    {
        [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.IUnknown)] object session);
    }

    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged(nint name, nint context);
        [PreserveSig] int OnIconPathChanged(nint path, nint context);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, int mute, nint context);
        [PreserveSig] int OnChannelVolumeChanged(uint count, nint levels, uint channel, nint context);
        [PreserveSig] int OnGroupingParamChanged(nint grouping, nint context);
        [PreserveSig] int OnStateChanged(int state);
        [PreserveSig] int OnSessionDisconnected(int reason);
    }
}
