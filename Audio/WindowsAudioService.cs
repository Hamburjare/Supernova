#if WINDOWS
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Concentus;
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
	private const int VadHoldFrames = 35; // Hold transmission for ~700ms after voice stops (35 frames * 20ms)
	private const int VadPlateauFrames = 10; // ~200ms at full volume before fade begins

	private readonly AudioSettings _settings = new();
	private readonly Dictionary<string, int> _userVolumes = new();
	private readonly Dictionary<string, (WaveOutEvent player, BufferedWaveProvider buffer, IOpusDecoder decoder, VolumeWaveProvider16 volumeProvider)> _userPlayback = new();
	private readonly object _lock = new();
	private readonly object _captureLock = new();

	private WasapiCapture? _capture;
	private IOpusEncoder? _encoder;
	private uint _sequenceNumber;
	private float _currentMicLevel;
	private bool _isVoiceDetected;
	private int _vadHoldCounter; // Frames to keep transmitting after voice stops
	private bool _isDisposed;
	private string? _localUserId;

	// Reusable buffers for the audio hot path
	private float[] _decodeBuffer = new float[FrameSamples * 2]; // max stereo
	private byte[] _pcmByteBuffer = new byte[FrameSamples * 2 * 2]; // max stereo * 16-bit

	// Ring buffer for resampling/buffering captured audio (accessed under _captureLock)
	private float[] _captureRingBuffer = new float[FrameSamples * 16];
	private int _captureRingCount;

	// Fade in/out for smooth VAD transitions (prevents clicks/pops)
	private const int FadeSamples = 480; // 10ms fade at 48kHz
	private bool _wasTransmitting;

	// Reusable buffers for capture hot path (eliminates per-frame GC allocations)
	private readonly float[] _frameBuffer = new float[FrameSamples];
	private readonly byte[] _opusEncodeBuffer = new byte[MaxOpusFrameSize];

	// Smoothed RMS for VAD to prevent gate flutter near threshold
	private float _smoothedRms;
	private const float RmsSmoothingAlpha = 0.3f;

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
		using var enumerator = new MMDeviceEnumerator();

		try
		{
			using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
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
				device.Dispose();
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
		using var enumerator = new MMDeviceEnumerator();

		try
		{
			using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
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
				device.Dispose();
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
			using var enumerator = new MMDeviceEnumerator();
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

			_encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
			_encoder.Bitrate = 32000;
			_encoder.Complexity = 5;
			_encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
			_encoder.UseVBR = true;

			_capture = new WasapiCapture(device)
			{
				ShareMode = AudioClientShareMode.Shared
			};

			_capture.DataAvailable += OnDataAvailable;
			_capture.RecordingStopped += OnRecordingStopped;

			lock (_captureLock)
			{
				_captureRingCount = 0;
			}
			_sequenceNumber = 0;
			_wasTransmitting = false;
			_smoothedRms = 0;
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
			// Update existing playback stream volume in real time
			if (_userPlayback.TryGetValue(userId, out var playback))
			{
				playback.volumeProvider.Volume = volume / 100f * (_settings.OutputVolume / 100f);
			}
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

				var decoder = OpusCodecFactory.CreateDecoder((int)frame.SampleRate, frame.Channels);
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

				playback = (player, buffer, decoder, volumeProvider);
				_userPlayback[fromUserId] = playback;
			}

			try
			{
				// Ensure player is playing (might have stopped when buffer emptied during deafen)
				if (playback.player.PlaybackState != PlaybackState.Playing)
				{
					playback.player.Play();
				}

				// Decode Opus to PCM using reusable buffers
				var requiredFloats = FrameSamples * frame.Channels;
				if (_decodeBuffer.Length < requiredFloats)
					_decodeBuffer = new float[requiredFloats];

				var samplesDecoded = playback.decoder.Decode(frame.OpusData.AsSpan(), _decodeBuffer.AsSpan(0, requiredFloats), FrameSamples, false);

				if (samplesDecoded > 0)
				{
					var totalSamples = samplesDecoded * frame.Channels;
					var requiredBytes = totalSamples * 2;
					if (_pcmByteBuffer.Length < requiredBytes)
						_pcmByteBuffer = new byte[requiredBytes];

					for (int i = 0; i < totalSamples; i++)
					{
						short sample = (short)(Math.Clamp(_decodeBuffer[i], -1f, 1f) * short.MaxValue);
						_pcmByteBuffer[i * 2] = (byte)(sample & 0xFF);
						_pcmByteBuffer[i * 2 + 1] = (byte)(sample >> 8);
					}
					playback.buffer.AddSamples(_pcmByteBuffer, 0, requiredBytes);
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
		lock (_captureLock)
		{
			EnsureRingCapacity(resampled.Length);
			Array.Copy(resampled, 0, _captureRingBuffer, _captureRingCount, resampled.Length);
			_captureRingCount += resampled.Length;
		}

		// Process complete frames
		while (true)
		{
			lock (_captureLock)
			{
				if (_captureRingCount < FrameSamples)
					break;
				Array.Copy(_captureRingBuffer, 0, _frameBuffer, 0, FrameSamples);
				_captureRingCount -= FrameSamples;
				if (_captureRingCount > 0)
					Array.Copy(_captureRingBuffer, FrameSamples, _captureRingBuffer, 0, _captureRingCount);
			}

			// Calculate RMS
			float rms = CalculateRms(_frameBuffer);
			_currentMicLevel = rms;
			MicLevelChanged?.Invoke(this, rms);

			// Smooth RMS with exponential moving average to prevent gate flutter
			_smoothedRms += RmsSmoothingAlpha * (rms - _smoothedRms);

			// Voice activity detection with hysteresis using smoothed RMS
			bool isActivelyVoiced = _smoothedRms >= _settings.MicSensitivity;
			if (isActivelyVoiced)
			{
				_isVoiceDetected = true;
				_vadHoldCounter = VadHoldFrames;
			}
			else if (_vadHoldCounter > 0)
			{
				_vadHoldCounter--;
				if (_vadHoldCounter == 0)
					_isVoiceDetected = false;
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
				shouldTransmit = true;
			}

			if (shouldTransmit)
			{
				// Fade-in on voice onset to prevent click
				if (isTransitioningIn)
				{
					for (int i = 0; i < Math.Min(FadeSamples, FrameSamples); i++)
					{
						_frameBuffer[i] *= (float)i / FadeSamples;
					}
				}
				else if (isTransitioningOut)
				{
					// Quick fade-out on final frame
					int startFade = Math.Max(0, FrameSamples - FadeSamples);
					for (int i = startFade; i < FrameSamples; i++)
					{
						_frameBuffer[i] *= (float)(FrameSamples - i) / FadeSamples;
					}
				}

				// Gradual fade during VAD hold period for smooth trail-off
				// After the plateau period, progressively reduce volume to zero
				int fadeZone = VadHoldFrames - VadPlateauFrames;
				if (!isActivelyVoiced && _vadHoldCounter > 0 && _vadHoldCounter < fadeZone)
				{
					float holdFade = (float)_vadHoldCounter / fadeZone;
					for (int i = 0; i < FrameSamples; i++)
					{
						_frameBuffer[i] *= holdFade;
					}
				}

				// Clamp in-place and encode to Opus
				for (int i = 0; i < FrameSamples; i++)
				{
					_frameBuffer[i] = Math.Clamp(_frameBuffer[i], -1f, 1f);
				}

				var encodedLength = _encoder.Encode(_frameBuffer.AsSpan(), FrameSamples, _opusEncodeBuffer.AsSpan(), MaxOpusFrameSize);

				if (encodedLength > 0)
				{
					var opusData = new byte[encodedLength];
					Array.Copy(_opusEncodeBuffer, opusData, encodedLength);

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

	private void EnsureRingCapacity(int additionalSamples)
	{
		var required = _captureRingCount + additionalSamples;
		if (required <= _captureRingBuffer.Length)
			return;
		var newSize = Math.Max(required, _captureRingBuffer.Length * 2);
		var newBuffer = new float[newSize];
		Array.Copy(_captureRingBuffer, 0, newBuffer, 0, _captureRingCount);
		_captureRingBuffer = newBuffer;
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
			foreach (var (player, _, _, _) in _userPlayback.Values)
			{
				player.Stop();
				player.Dispose();
			}
			_userPlayback.Clear();
		}
	}
}
#endif
