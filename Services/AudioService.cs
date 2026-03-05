using NAudio.Wave;

namespace Supernova.Services;

public class AudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private BufferedWaveProvider? _playbackBuffer;
    private WaveOutEvent? _waveOut;

    private uint _sampleRate = 16000;
    private int _frameMs = 50;
    private uint _sendSeq;

    public event Action<uint, uint, byte, float, List<byte>>? OnFrameCaptured;

    public void Configure(uint sampleRate, int frameMs)
    {
        _sampleRate = sampleRate;
        _frameMs = frameMs;
    }

    public void StartCapture()
    {
        StopCapture();

        int sr = (int)_sampleRate;
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(sr, 16, 1),
            BufferMilliseconds = _frameMs
        };

        _waveIn.DataAvailable += (s, e) =>
        {
            if (e.BytesRecorded == 0) return;

            float rms = 0;
            int sampleCount = e.BytesRecorded / 2;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float norm = sample / 32768f;
                rms += norm * norm;
            }
            rms = sampleCount > 0 ? MathF.Sqrt(rms / sampleCount) : 0;

            var pcm = new List<byte>(e.BytesRecorded);
            for (int i = 0; i < e.BytesRecorded; i++)
                pcm.Add(e.Buffer[i]);

            _sendSeq++;
            OnFrameCaptured?.Invoke(_sendSeq, _sampleRate, 1, rms, pcm);
        };

        _waveIn.StartRecording();
    }

    public void StopCapture()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        _sendSeq = 0;
    }

    public void StartPlayback()
    {
        StopPlayback();

        _playbackBuffer = new BufferedWaveProvider(new WaveFormat((int)_sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();
    }

    public void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _playbackBuffer = null;
    }

    public void PlayFrame(byte[] pcm16le)
    {
        _playbackBuffer?.AddSamples(pcm16le, 0, pcm16le.Length);
    }

    public void Dispose()
    {
        StopCapture();
        StopPlayback();
    }
}
