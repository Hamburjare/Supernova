#if WINDOWS
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Concentus.Structs;
using Concentus.Enums;

namespace Supernova.Audio;

/// <summary>
/// Windows audio service implementation using NAudio and Opus codec.
/// </summary>
public sealed class WindowsAudioService : IAudioService
{
	private const int SampleRate = 48000;
	private const int Channels = 1; // Mono for voice
	private const int FrameSizeMs = 20;
	private const int FrameSamples = SampleRate * FrameSizeMs / 1000;
	private const int MaxOpusFrameSize = 4000;
	private const int VadHoldFrames = 25; // Hold transmission for ~500ms after voice stops (25 frames * 20ms)

	private readonly AudioSettings _settings = new();
	private readonly Dictionary<string, int> _userVolumes = new();
	private readonly Dictionary<string, (WaveOutEvent player, BufferedWaveProvider buffer, OpusDecoder decoder)> _userPlayback = new();
	private readonly object _lock = new();

	private WasapiCapture? _capture;
	private OpusEncoder? _encoder;
	private uint _sequenceNumber;
	private float _currentMicLevel;
	private bool _isVoiceDetected;
	private int _vadHoldCounter; // Frames to keep transmitting after voice stops
	private bool _isDisposed;
	private string? _localUserId;

	// Ring buffer for resampling/buffering captured audio
	private readonly List<float> _captureBuffer = new();

	// Fade in/out for smooth VAD transitions (prevents clicks/pops)
	private const int FadeSamples = 240; // 5ms fade at 48kHz
	private bool _wasTransmitting;

	public AudioSettings Settings => _settings;
	public bool IsCapturing => _capture is not null;
	public float CurrentMicLevel => _currentMicLevel;
	public bool IsVoiceDetected => _isVoiceDetected;

	public event EventHandler<AudioFrame>? AudioFrameReady;
	public event EventHandler<float>? MicLevelChanged;

	/// <summary>
	/// Sets the local user ID to filter out self-audio when HearSelf is disabled.
	/// </summary>
	public void SetLocalUserId(string userId) => _localUserId = userId;

	public IReadOnlyList<AudioDevice> GetInputDevices()
	{
		var devices = new List<AudioDevice>();
		var enumerator = new MMDeviceEnumerator();

		try
		{
			var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
			var defaultId = defaultDevice?.ID;

			foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
			{
				devices.Add(new AudioDevice
				{
					Id = device.ID,
					Name = device.FriendlyName,
					IsDefault = device.ID == defaultId,
					IsInput = true
				});
			}
		}
		catch
		{
			// No audio devices available
		}

		return devices;
	}

	public IReadOnlyList<AudioDevice> GetOutputDevices()
	{
		var devices = new List<AudioDevice>();
		var enumerator = new MMDeviceEnumerator();

		try
		{
			var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
			var defaultId = defaultDevice?.ID;

			foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				devices.Add(new AudioDevice
				{
					Id = device.ID,
					Name = device.FriendlyName,
					IsDefault = device.ID == defaultId,
					IsInput = false
				});
			}
		}
		catch
		{
			// No audio devices available
		}

		return devices;
	}

	public void StartCapture()
	{
		if (_capture is not null)
		{
			return;
		}

		try
		{
			var enumerator = new MMDeviceEnumerator();
			MMDevice? device;

			if (!string.IsNullOrEmpty(_settings.InputDeviceId))
			{
				device = enumerator.GetDevice(_settings.InputDeviceId);
			}
			else
			{
				device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
			}

			if (device is null)
			{
				return;
			}

			_encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
			{
				Bitrate = 32000,
				Complexity = 5,
				SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
				UseVBR = true
			};

			_capture = new WasapiCapture(device)
			{
				ShareMode = AudioClientShareMode.Shared
			};

			_capture.DataAvailable += OnDataAvailable;
			_capture.RecordingStopped += OnRecordingStopped;

			_captureBuffer.Clear();
			_sequenceNumber = 0;
			_wasTransmitting = false;
			_capture.StartRecording();
		}
		catch
		{
			_capture?.Dispose();
			_capture = null;
			_encoder = null;
		}
	}

	public void StopCapture()
	{
		if (_capture is null)
		{
			return;
		}

		_capture.StopRecording();
		_capture.DataAvailable -= OnDataAvailable;
		_capture.RecordingStopped -= OnRecordingStopped;
		_capture.Dispose();
		_capture = null;
		_encoder = null;

		_currentMicLevel = 0;
		_isVoiceDetected = false;
		MicLevelChanged?.Invoke(this, 0);
	}

	public void SetUserVolume(string userId, int volume)
	{
		volume = Math.Clamp(volume, 0, 200);
		lock (_lock)
		{
			_userVolumes[userId] = volume;
		}
	}

	public int GetUserVolume(string userId)
	{
		lock (_lock)
		{
			return _userVolumes.TryGetValue(userId, out var volume) ? volume : 100;
		}
	}

	public void PlayAudioFrame(string fromUserId, AudioFrame frame)
	{
		if (_settings.IsDeafened)
		{
			return;
		}

		// Skip self-audio if HearSelf is disabled
		if (!_settings.HearSelf && fromUserId == _localUserId)
		{
			return;
		}

		lock (_lock)
		{
			if (!_userPlayback.TryGetValue(fromUserId, out var playback))
			{
				// Create new playback stream for this user with larger buffer to prevent stutter
				var waveFormat = new WaveFormat((int)frame.SampleRate, 16, frame.Channels);
				var buffer = new BufferedWaveProvider(waveFormat)
				{
					BufferDuration = TimeSpan.FromSeconds(5),
					DiscardOnBufferOverflow = true
				};

				var decoder = new OpusDecoder((int)frame.SampleRate, frame.Channels);
				var player = new WaveOutEvent
				{
					DesiredLatency = 200,
					NumberOfBuffers = 4
				};

				// Apply user volume
				var userVolume = _userVolumes.TryGetValue(fromUserId, out var vol) ? vol : 100;
				var volumeProvider = new VolumeWaveProvider16(buffer)
				{
					Volume = userVolume / 100f * (_settings.OutputVolume / 100f)
				};

				player.Init(volumeProvider);
				player.Play();

				playback = (player, buffer, decoder);
				_userPlayback[fromUserId] = playback;
			}

			try
			{
				// Ensure player is playing (might have stopped when buffer emptied during deafen)
				if (playback.player.PlaybackState != PlaybackState.Playing)
				{
					playback.player.Play();
				}

				// Decode Opus to PCM
				var pcmBuffer = new short[FrameSamples * frame.Channels];
				var samplesDecoded = playback.decoder.Decode(frame.OpusData, 0, frame.OpusData.Length, pcmBuffer, 0, FrameSamples);

				if (samplesDecoded > 0)
				{
					// Convert short[] to byte[] for BufferedWaveProvider
					var byteBuffer = new byte[samplesDecoded * frame.Channels * 2];
					Buffer.BlockCopy(pcmBuffer, 0, byteBuffer, 0, byteBuffer.Length);
					playback.buffer.AddSamples(byteBuffer, 0, byteBuffer.Length);
				}
			}
			catch
			{
				// Decode error - skip frame
			}
		}
	}

	private void OnDataAvailable(object? sender, WaveInEventArgs e)
	{
		if (_settings.IsMuted || _encoder is null)
		{
			return;
		}

		// Convert captured audio to float samples
		var captureFormat = _capture!.WaveFormat;
		var samples = ConvertToFloatSamples(e.Buffer, e.BytesRecorded, captureFormat);

		// Apply mic volume
		var volumeScale = _settings.MicrophoneVolume / 100f;
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] *= volumeScale;
		}

		// Resample to target rate if needed and add to buffer
		var resampled = ResampleIfNeeded(samples, captureFormat.SampleRate, SampleRate, captureFormat.Channels);
		_captureBuffer.AddRange(resampled);

		// Process complete frames
		while (_captureBuffer.Count >= FrameSamples)
		{
			var frameData = _captureBuffer.Take(FrameSamples).ToArray();
			_captureBuffer.RemoveRange(0, FrameSamples);

			// Calculate RMS
			float rms = CalculateRms(frameData);
			_currentMicLevel = rms;
			MicLevelChanged?.Invoke(this, rms);

			// Voice activity detection with hysteresis
			if (rms >= _settings.MicSensitivity)
			{
				_isVoiceDetected = true;
				_vadHoldCounter = VadHoldFrames;
			}
			else if (_vadHoldCounter > 0)
			{
				_vadHoldCounter--;
				// Keep transmitting during hold period
			}
			else
			{
				_isVoiceDetected = false;
			}

			// Determine if we should transmit this frame
			bool shouldTransmit = !_settings.VoiceActivityDetection || _isVoiceDetected || _vadHoldCounter > 0;

			// Handle VAD transitions with fade to prevent clicks/pops
			bool isTransitioningIn = shouldTransmit && !_wasTransmitting;
			bool isTransitioningOut = !shouldTransmit && _wasTransmitting;

			// If transitioning out, send one final fade-out frame
			if (isTransitioningOut)
			{
				shouldTransmit = true; // Send one more frame with fade-out
			}

			if (shouldTransmit)
			{
				// Apply fade envelope for smooth transitions
				var fadedFrame = new float[FrameSamples];
				Array.Copy(frameData, fadedFrame, FrameSamples);

				if (isTransitioningIn)
				{
					// Fade in: ramp up over FadeSamples
					for (int i = 0; i < Math.Min(FadeSamples, FrameSamples); i++)
					{
						fadedFrame[i] *= (float)i / FadeSamples;
					}
				}
				else if (isTransitioningOut)
				{
					// Fade out: ramp down over FadeSamples
					int startFade = Math.Max(0, FrameSamples - FadeSamples);
					for (int i = startFade; i < FrameSamples; i++)
					{
						fadedFrame[i] *= (float)(FrameSamples - i) / FadeSamples;
					}
				}

				// Encode to Opus
				var pcmShort = new short[FrameSamples];
				for (int i = 0; i < FrameSamples; i++)
				{
					pcmShort[i] = (short)(Math.Clamp(fadedFrame[i], -1f, 1f) * short.MaxValue);
				}

				var opusBuffer = new byte[MaxOpusFrameSize];
				var encodedLength = _encoder.Encode(pcmShort, 0, FrameSamples, opusBuffer, 0, opusBuffer.Length);

				if (encodedLength > 0)
				{
					var opusData = new byte[encodedLength];
					Array.Copy(opusBuffer, opusData, encodedLength);

					var frame = new AudioFrame
					{
						Sequence = _sequenceNumber++,
						SampleRate = SampleRate,
						Channels = Channels,
						Rms = rms,
						OpusData = opusData
					};

					AudioFrameReady?.Invoke(this, frame);
				}
			}

			_wasTransmitting = shouldTransmit && !isTransitioningOut;
		}
	}

	private void OnRecordingStopped(object? sender, StoppedEventArgs e)
	{
		// Recording stopped (device unplugged, etc.)
		_currentMicLevel = 0;
		_isVoiceDetected = false;
		MicLevelChanged?.Invoke(this, 0);
	}

	private static float[] ConvertToFloatSamples(byte[] buffer, int length, WaveFormat format)
	{
		var bytesPerSample = format.BitsPerSample / 8;
		var sampleCount = length / bytesPerSample;
		var samples = new float[sampleCount / format.Channels]; // Convert to mono

		for (int i = 0; i < samples.Length; i++)
		{
			float sum = 0;
			for (int ch = 0; ch < format.Channels; ch++)
			{
				var byteIndex = (i * format.Channels + ch) * bytesPerSample;

				float sample = format.BitsPerSample switch
				{
					16 => BitConverter.ToInt16(buffer, byteIndex) / (float)short.MaxValue,
					32 when format.Encoding == WaveFormatEncoding.IeeeFloat => BitConverter.ToSingle(buffer, byteIndex),
					32 => BitConverter.ToInt32(buffer, byteIndex) / (float)int.MaxValue,
					_ => 0
				};

				sum += sample;
			}
			samples[i] = sum / format.Channels; // Average to mono
		}

		return samples;
	}

	private static float[] ResampleIfNeeded(float[] samples, int sourceRate, int targetRate, int sourceChannels)
	{
		if (sourceRate == targetRate)
		{
			return samples;
		}

		// Simple linear resampling
		var ratio = (double)sourceRate / targetRate;
		var outputLength = (int)(samples.Length / ratio);
		var output = new float[outputLength];

		for (int i = 0; i < outputLength; i++)
		{
			var sourceIndex = i * ratio;
			var index = (int)sourceIndex;
			var fraction = (float)(sourceIndex - index);

			if (index + 1 < samples.Length)
			{
				output[i] = samples[index] * (1 - fraction) + samples[index + 1] * fraction;
			}
			else if (index < samples.Length)
			{
				output[i] = samples[index];
			}
		}

		return output;
	}

	private static float CalculateRms(float[] samples)
	{
		if (samples.Length == 0)
		{
			return 0;
		}

		double sum = 0;
		foreach (var sample in samples)
		{
			sum += sample * sample;
		}

		return (float)Math.Sqrt(sum / samples.Length);
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		StopCapture();

		lock (_lock)
		{
			foreach (var (player, _, _) in _userPlayback.Values)
			{
				player.Stop();
				player.Dispose();
			}
			_userPlayback.Clear();
		}
	}
}
#endif
