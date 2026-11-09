using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Radios;

namespace DynamicIsland.Services;

public sealed class SystemControlService : IDisposable
{
    private readonly List<Radio> _radios = [];
    private IAudioEndpointVolume? _volume;

    public event Action? RadioChanged;
    public string Status { get; private set; } = "";

    public async Task InitializeAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                Status = "Windows не разрешил управление радиомодулями";
                return;
            }
            var radios = await Radio.GetRadiosAsync();
            foreach (var radio in radios)
            {
                if (radio.Kind is not (RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband)) continue;
                _radios.Add(radio);
                radio.StateChanged += (_, _) => RadioChanged?.Invoke();
            }
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public bool? GetRadioState(RadioKind kind) => _radios.FirstOrDefault(x => x.Kind == kind)?.State switch
    {
        RadioState.On => true,
        RadioState.Off => false,
        _ => null
    };

    public async Task<bool> SetRadioStateAsync(RadioKind kind, bool enabled)
    {
        var changed = false;
        foreach (var radio in _radios.Where(x => x.Kind == kind))
        {
            try
            {
                var result = await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off);
                changed |= result == RadioAccessStatus.Allowed;
            }
            catch (Exception ex) { Status = ex.Message; }
        }
        RadioChanged?.Invoke();
        return changed;
    }

    public double GetVolume()
    {
        try
        {
            EnsureVolume();
            Marshal.ThrowExceptionForHR(_volume!.GetMasterVolumeLevelScalar(out var level));
            return Math.Round(level * 100);
        }
        catch { return 0; }
    }

    public void SetVolume(double value)
    {
        try
        {
            EnsureVolume();
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(_volume!.SetMasterVolumeLevelScalar((float)(value / 100), ref context));
        }
        catch { }
    }

    public bool GetMute()
    {
        try
        {
            EnsureVolume();
            Marshal.ThrowExceptionForHR(_volume!.GetMute(out var mute));
            return mute;
        }
        catch { return false; }
    }

    public void SetMute(bool mute)
    {
        try
        {
            EnsureVolume();
            var context = Guid.Empty;
            _volume!.SetMute(mute, ref context);
        }
        catch { }
    }

    private void EnsureVolume()
    {
        if (_volume is not null) return;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out _volume));
        Marshal.ReleaseComObject(device);
        Marshal.ReleaseComObject(enumerator);
    }

    public Task<int?> GetBrightnessAsync() => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            var item = results.Cast<ManagementObject>().FirstOrDefault();
            return item is null ? null : (int?)Convert.ToInt32(item["CurrentBrightness"]);
        }
        catch { return null; }
    });

    public Task<bool> SetBrightnessAsync(int value) => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            var item = results.Cast<ManagementObject>().FirstOrDefault();
            if (item is null) return false;
            item.InvokeMethod("WmiSetBrightness", [uint.MaxValue, (byte)Math.Clamp(value, 0, 100)]);
            return true;
        }
        catch { return false; }
    });

    public static (int Percent, bool Charging, bool Available) GetBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryLifePercent == 255)
            return (0, false, false);
        return (status.BatteryLifePercent, status.AclLineStatus == 1, true);
    }

    public void Dispose()
    {
        if (_volume is not null) Marshal.ReleaseComObject(_volume);
    }
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
}

internal enum DataFlow { Render, Capture, All }
internal enum Role { Console, Multimedia, Communications }

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume instance);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out int channelCount);
    [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float level);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(int channel, float level, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(int channel, out float level);
    [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out int step, out int stepCount);
    [PreserveSig] int VolumeStepUp(ref Guid eventContext);
    [PreserveSig] int VolumeStepDown(ref Guid eventContext);
    [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
}
