namespace Supernova.Audio;

/// <summary>
/// Represents an audio device (input or output).
/// </summary>
public sealed class AudioDevice
{
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";
	public bool IsDefault { get; init; }
	public bool IsInput { get; init; }
}

/// <summary>
/// Audio frame for transmission.
/// </summary>
public sealed class AudioFrame
{
	public uint Sequence { get; init; }
	public uint SampleRate { get; init; }
	public byte Channels { get; init; }
	public float Rms { get; init; }
	public byte[] OpusData { get; init; } = [];
}

/// <summary>
/// Settings for audio capture and playback.
/// </summary>
public sealed class AudioSettings
{
	/// <summary>Input device ID (null for default)</summary>
	public string? InputDeviceId { get; set; }

	/// <summary>Output device ID (null for default)</summary>
	public string? OutputDeviceId { get; set; }

	/// <summary>Microphone volume 0-100</summary>
	public int MicrophoneVolume { get; set; } = 100;

	/// <summary>Output volume 0-100</summary>
	public int OutputVolume { get; set; } = 100;

	/// <summary>Mic sensitivity threshold (0.0 - 1.0). Audio below this is not transmitted.</summary>
	public float MicSensitivity { get; set; } = 0.02f;

	/// <summary>Whether the mic is muted</summary>
	public bool IsMuted { get; set; }

	/// <summary>Whether output is deafened</summary>
	public bool IsDeafened { get; set; }

	/// <summary>Whether to hear your own voice (loopback). Disabled by default.</summary>
	public bool HearSelf { get; set; } = false;

	/// <summary>Enable voice activity detection (only transmit when speaking)</summary>
	public bool VoiceActivityDetection { get; set; } = true;

	/// <summary>Enable echo cancellation</summary>
	public bool EchoCancellation { get; set; } = true;

	/// <summary>Enable noise suppression</summary>
	public bool NoiseSuppression { get; set; } = true;
}

/// <summary>
/// Interface for audio service implementations (platform-specific).
/// </summary>
public interface IAudioService : IDisposable
{
	/// <summary>Gets available input devices.</summary>
	IReadOnlyList<AudioDevice> GetInputDevices();

	/// <summary>Gets available output devices.</summary>
	IReadOnlyList<AudioDevice> GetOutputDevices();

	/// <summary>Current audio settings.</summary>
	AudioSettings Settings { get; }

	/// <summary>Whether audio capture is currently active.</summary>
	bool IsCapturing { get; }

	/// <summary>Current microphone RMS level (0.0 - 1.0).</summary>
	float CurrentMicLevel { get; }

	/// <summary>Whether voice activity is detected.</summary>
	bool IsVoiceDetected { get; }

	/// <summary>Starts audio capture.</summary>
	void StartCapture();

	/// <summary>Stops audio capture.</summary>
	void StopCapture();

	/// <summary>Sets the volume for a specific user (0-200, 100 = normal).</summary>
	void SetUserVolume(string userId, int volume);

	/// <summary>Gets the volume for a specific user.</summary>
	int GetUserVolume(string userId);

	/// <summary>Plays received audio from a user.</summary>
	void PlayAudioFrame(string fromUserId, AudioFrame frame);

	/// <summary>Sets the local user ID to filter out self-audio.</summary>
	void SetLocalUserId(string userId);

	/// <summary>Raised when an audio frame is ready to send.</summary>
	event EventHandler<AudioFrame>? AudioFrameReady;

	/// <summary>Raised when mic level changes (for UI updates).</summary>
	event EventHandler<float>? MicLevelChanged;
}
