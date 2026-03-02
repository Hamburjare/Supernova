namespace Supernova.Audio;

/// <summary>
/// Factory for creating the audio service.
/// </summary>
public static class AudioServiceFactory
{
	/// <summary>
	/// Creates the Windows audio service.
	/// </summary>
	public static IAudioService Create() => new WindowsAudioService();
}
