using EHub.Diagnostics;
using NAudio.CoreAudioApi;

namespace EHub.Hardware.Providers;

/// <summary>主音量的电平与静音状态。</summary>
internal readonly record struct VolumeReading(double? Level, bool Muted);

/// <summary>
/// 系统主音量。原版走 pycaw（comtypes 手搓 COM 接口并缓存），
/// NAudio 把同一套 CoreAudio 接口包好了，缓存逻辑照旧。
/// </summary>
internal sealed class VolumeProvider : IDisposable
{
    /// <summary>端点可能稍后才出现（例如音频服务刚重启），失败后隔一段时间再试。</summary>
    private const long RetryAfterMilliseconds = 10_000;

    private readonly object _gate = new();

    private MMDevice? _device;
    private VolumeReading _last;
    private long _nextAttempt;

    public VolumeReading Read()
    {
        lock (_gate)
        {
            if (_device is null)
            {
                if (Environment.TickCount64 < _nextAttempt)
                {
                    return _last;
                }

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                catch (Exception ex)
                {
                    Log.Warn($"音频端点不可用: {ex.Message}");
                    _device = null;
                    _nextAttempt = Environment.TickCount64 + RetryAfterMilliseconds;
                    return _last;
                }
            }

            try
            {
                var endpoint = _device.AudioEndpointVolume;

                // 静音时电平不会归零，两个值要分开读，否则界面上没法区分「静音」和「音量 0」
                _last = new VolumeReading(
                    Math.Round(endpoint.MasterVolumeLevelScalar * 100),
                    endpoint.Mute);

                return _last;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取音量失败: {ex.Message}");
                _device.Dispose();
                _device = null;
                _nextAttempt = Environment.TickCount64 + RetryAfterMilliseconds;
                return _last;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
