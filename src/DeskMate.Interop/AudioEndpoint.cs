using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 默认输出设备的主音量与静音（MMDevice API + <c>IAudioEndpointVolume</c>，纯 COM interop，不落任何设置）。
/// <para>
/// 不注册变更回调：<c>IAudioEndpointVolumeCallback</c> 的生命周期管理脆，而且换默认设备
/// （插拔耳机）后回调静默失效 —— 调用方用 500ms~1s 的间隔轮询反而更稳；插拔设备后第一次调用失败
/// 会自动重拿一次端点。全部调用只在 UI 线程上发生。
/// </para>
/// </summary>
public static class AudioEndpoint
{
    /// <summary>eRender。</summary>
    private const int Render = 0;

    /// <summary>eMultimedia：系统「默认设备」设置里指向的那一类。</summary>
    private const int Multimedia = 1;

    private const int ClsCtxAll = 0x17;

    private static readonly Guid VolumeInterfaceId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>缓存的默认端点，换设备后第一次调用失败时整体重拿。</summary>
    private static IMMDevice? _endpoint;

    private static IAudioEndpointVolume? _volume;

    public static bool TryGetVolume(out int percent, out bool muted)
    {
        percent = 0;
        muted = false;

        var volume = Acquire(force: false);
        if (volume is null)
        {
            return false;
        }

        if (volume.GetMasterVolumeLevelScalar(out var scalar) != 0 || volume.GetMute(out muted) != 0)
        {
            volume = Acquire(force: true);
            if (volume is null
                || volume.GetMasterVolumeLevelScalar(out scalar) != 0
                || volume.GetMute(out muted) != 0)
            {
                return false;
            }
        }

        percent = (int)Math.Round(scalar * 100);
        return true;
    }

    /// <summary>设置主音量，0~100。</summary>
    public static bool TrySetVolume(int percent)
    {
        var volume = Acquire(force: false);
        if (volume is null)
        {
            return false;
        }

        var scalar = Math.Clamp(percent, 0, 100) / 100f;
        var context = Guid.Empty;
        if (volume.SetMasterVolumeLevelScalar(scalar, ref context) != 0)
        {
            volume = Acquire(force: true);
            if (volume is null)
            {
                return false;
            }

            context = Guid.Empty;
            return volume.SetMasterVolumeLevelScalar(scalar, ref context) == 0;
        }

        return true;
    }

    public static bool TrySetMute(bool mute)
    {
        var volume = Acquire(force: false);
        if (volume is null)
        {
            return false;
        }

        var context = Guid.Empty;
        if (volume.SetMute(mute, ref context) != 0)
        {
            volume = Acquire(force: true);
            if (volume is null)
            {
                return false;
            }

            context = Guid.Empty;
            return volume.SetMute(mute, ref context) == 0;
        }

        return true;
    }

    private static IAudioEndpointVolume? Acquire(bool force)
    {
        if (!force && _volume is not null)
        {
            return _volume;
        }

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                ?? throw new InvalidOperationException("MMDeviceEnumerator COM 类不可用");
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0)
            {
                return null;
            }

            var iid = VolumeInterfaceId;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var iface) != 0)
            {
                return null;
            }

            _endpoint = device;
            _volume = (IAudioEndpointVolume)iface;
            return _volume;
        }
        catch
        {
            // 音频服务没跑/端点不存在是正常场景（无输出设备的机器），按「读不到」处理。
            _volume = null;
            _endpoint = null;
            return null;
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr collection);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    /// <summary>端点音量接口。方法顺序必须与 vtable 一致（含用不到的也在声明里占位）。</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr callback);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr callback);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupport);

        [PreserveSig]
        int GetVolumeRange(out float minLevel, out float maxLevel, out float increment);
    }
}
