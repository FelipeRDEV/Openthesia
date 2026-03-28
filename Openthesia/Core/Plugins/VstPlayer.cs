using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Openthesia.Enums;
using Openthesia.Settings;

namespace Openthesia.Core.Plugins;

public static class VstPlayer
{
    private static MixingSampleProvider _mixingSampleProvider;
    private static VolumeSampleProvider _volumeSampleProvider;

    private static WaveOutEvent _waveOut;
    public static WaveOutEvent WaveOut => _waveOut;

    private static AsioOut _asioOut;
    public static AsioOut AsioOut => _asioOut;

    public static PluginsChain? PluginsChain { get; private set; }

    public static void Initialize()
    {
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(CoreSettings.SampleRate, 2))
        {
            ReadFully = true
        };

        _mixingSampleProvider = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(CoreSettings.SampleRate, 2))
        {
            ReadFully = true
        };

        _volumeSampleProvider = new VolumeSampleProvider(_mixingSampleProvider)
        {
            Volume = CoreSettings.MasterVolume
        };

        PluginsChain = new PluginsChain(mixer);
        _mixingSampleProvider.AddMixerInput(PluginsChain);

        if (AudioDriverManager.AudioDriverType == AudioDriverTypes.WaveOut)
        {
            _asioOut?.Stop();
            _asioOut?.Dispose();

            _waveOut = new WaveOutEvent();
            _waveOut.DesiredLatency = CoreSettings.WaveOutLatency;
            _waveOut.Init(_volumeSampleProvider);
            _waveOut.Play();
        }
        else if (AudioDriverManager.AudioDriverType == AudioDriverTypes.ASIO)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();

            _asioOut = new AsioOut(AudioDriverManager.SelectedAsioDriverName);
            _asioOut.Init(_volumeSampleProvider);
            _asioOut.Play();
        }
    }

    public static void ChangeLatency(int newLatency)
    {
        bool isRunning = _waveOut.PlaybackState == PlaybackState.Playing || _waveOut.PlaybackState == PlaybackState.Paused;
        if (isRunning)
        {
            _waveOut.Stop();
        }

        _waveOut.DesiredLatency = newLatency;
        _waveOut.Init(_volumeSampleProvider);
        _waveOut.Play();
    }

    public static void SetVolume(float volume)
    {
        if (_volumeSampleProvider != null)
        {
            _volumeSampleProvider.Volume = Math.Clamp(volume, 0f, 10f);
        }
    }
}
