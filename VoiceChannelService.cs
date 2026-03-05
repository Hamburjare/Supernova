using SpacetimeDB;
using SpacetimeDB.Types;
using Supernova.Audio;

namespace Supernova;

/// <summary>
/// Service that coordinates voice channel functionality with SpacetimeDB.
/// </summary>
public sealed class VoiceChannelService : IDisposable
{
	private readonly IAudioService _audioService;
	private DbConnection? _conn;
	private uint? _currentChannelId;
	private bool _isDisposed;
	private bool _isInitialized;

	// Track members in the current channel
	private readonly Dictionary<Identity, VoiceChannelMemberInfo> _channelMembers = new();
	private readonly object _lock = new();
	private Identity? _localIdentity;

	public event EventHandler<VoiceChannelMemberInfo>? MemberJoined;
	public event EventHandler<VoiceChannelMemberInfo>? MemberLeft;
	public event EventHandler<VoiceChannelMemberInfo>? MemberUpdated;
	public event EventHandler<float>? MicLevelChanged;
	public event EventHandler<(Identity User, float Level)>? UserSpeaking;
	public event EventHandler<bool>? ChannelStateChanged;
	public event EventHandler? AudioStateChanged;

	public IAudioService AudioService => _audioService;
	public uint? CurrentChannelId => _currentChannelId;
	public bool IsInChannel => _currentChannelId.HasValue;
	public IReadOnlyCollection<VoiceChannelMemberInfo> ChannelMembers
	{
		get
		{
			lock (_lock)
			{
				return _channelMembers.Values.ToList();
			}
		}
	}

	public VoiceChannelService()
	{
		_audioService = AudioServiceFactory.Create();
		_audioService.AudioFrameReady += OnAudioFrameReady;
		_audioService.MicLevelChanged += OnMicLevelChanged;
	}

	/// <summary>
	/// Initializes the service with a database connection.
	/// </summary>
	public void Initialize(DbConnection conn, Identity localIdentity)
	{
		if (_isInitialized)
		{
			throw new InvalidOperationException("VoiceChannelService is already initialized. Dispose and create a new instance.");
		}
		_isInitialized = true;

		_conn = conn;
		_localIdentity = localIdentity;
		_audioService.SetLocalUserId(localIdentity.ToString());

		// Register callbacks for voice channel events
		conn.Db.VoiceChannelMember.OnInsert += OnVoiceChannelMemberInserted;
		conn.Db.VoiceChannelMember.OnUpdate += OnVoiceChannelMemberUpdated;
		conn.Db.VoiceChannelMember.OnDelete += OnVoiceChannelMemberDeleted;
		conn.Db.AudioFrameEvent.OnInsert += OnAudioFrameReceived;
		conn.Db.UserVolumeSettings.OnInsert += OnUserVolumeSettingsChanged;
		conn.Db.UserVolumeSettings.OnUpdate += OnUserVolumeSettingsUpdated;

		// Register reducer callbacks
		conn.Reducers.OnJoinVoiceChannel += OnJoinVoiceChannelResult;
		conn.Reducers.OnLeaveVoiceChannel += OnLeaveVoiceChannelResult;
		conn.Reducers.OnSetVoiceMuted += OnSetVoiceMutedResult;
		conn.Reducers.OnSetVoiceDeafened += OnSetVoiceDeafenedResult;
	}

	/// <summary>
	/// Joins a voice channel.
	/// </summary>
	public void JoinChannel(uint channelId)
	{
		if (_conn is null)
		{
			throw new InvalidOperationException("Not connected to database");
		}

		_conn.Reducers.JoinVoiceChannel(channelId);
	}

	/// <summary>
	/// Leaves the current voice channel.
	/// </summary>
	public void LeaveChannel()
	{
		if (_conn is null || !_currentChannelId.HasValue)
		{
			return;
		}

		_audioService.StopCapture();
		_conn.Reducers.LeaveVoiceChannel();
	}

	/// <summary>
	/// Sets the mute state.
	/// </summary>
	public void SetMuted(bool muted)
	{
		_audioService.Settings.IsMuted = muted;
		_conn?.Reducers.SetVoiceMuted(muted);
	}

	/// <summary>
	/// Sets the deafened state.
	/// </summary>
	public void SetDeafened(bool deafened)
	{
		_audioService.Settings.IsDeafened = deafened;
		_conn?.Reducers.SetVoiceDeafened(deafened);
	}

	/// <summary>
	/// Sets the volume for a specific user.
	/// </summary>
	public void SetUserVolume(Identity targetUser, byte volume)
	{
		_audioService.SetUserVolume(targetUser.ToString(), volume);
		_conn?.Reducers.SetUserVolume(targetUser, volume);
	}

	private void OnJoinVoiceChannelResult(ReducerEventContext ctx, uint channelId)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (ctx.Event.Status is Status.Committed)
			{
				_currentChannelId = channelId;
				_audioService.StartCapture();
				RefreshChannelMembers(channelId);
				ChannelStateChanged?.Invoke(this, true);
			}
		});
	}

	private void OnLeaveVoiceChannelResult(ReducerEventContext ctx)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (ctx.Event.Status is Status.Committed)
			{
				_currentChannelId = null;
				_audioService.StopCapture();

				lock (_lock)
				{
					_channelMembers.Clear();
				}

				ChannelStateChanged?.Invoke(this, false);
			}
		});
	}

	private void OnSetVoiceMutedResult(ReducerEventContext ctx, bool muted)
	{
		// If reducer failed, revert local state and notify UI
		if (ctx.Event.Status is Status.Failed)
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				_audioService.Settings.IsMuted = !muted;
				AudioStateChanged?.Invoke(this, EventArgs.Empty);
			});
		}
	}

	private void OnSetVoiceDeafenedResult(ReducerEventContext ctx, bool deafened)
	{
		// If reducer failed, revert local state and notify UI
		if (ctx.Event.Status is Status.Failed)
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				_audioService.Settings.IsDeafened = !deafened;
				AudioStateChanged?.Invoke(this, EventArgs.Empty);
			});
		}
	}

	private void RefreshChannelMembers(uint channelId)
	{
		if (_conn is null)
		{
			return;
		}

		lock (_lock)
		{
			_channelMembers.Clear();

			foreach (var member in _conn.Db.VoiceChannelMember.Iter())
			{
				if (member.ChannelId == channelId)
				{
					var info = CreateMemberInfo(member);
					_channelMembers[member.UserId] = info;
				}
			}
		}
	}

	private void OnVoiceChannelMemberInserted(EventContext ctx, VoiceChannelMember member)
	{
		if (!_currentChannelId.HasValue || member.ChannelId != _currentChannelId.Value)
		{
			return;
		}

		MainThread.BeginInvokeOnMainThread(() =>
		{
			var info = CreateMemberInfo(member);
			lock (_lock)
			{
				_channelMembers[member.UserId] = info;
			}
			MemberJoined?.Invoke(this, info);
		});
	}

	private void OnVoiceChannelMemberUpdated(EventContext ctx, VoiceChannelMember oldMember, VoiceChannelMember newMember)
	{
		if (!_currentChannelId.HasValue || newMember.ChannelId != _currentChannelId.Value)
		{
			return;
		}

		MainThread.BeginInvokeOnMainThread(() =>
		{
			var info = CreateMemberInfo(newMember);
			lock (_lock)
			{
				_channelMembers[newMember.UserId] = info;
			}
			MemberUpdated?.Invoke(this, info);
		});
	}

	private void OnVoiceChannelMemberDeleted(EventContext ctx, VoiceChannelMember member)
	{
		if (!_currentChannelId.HasValue || member.ChannelId != _currentChannelId.Value)
		{
			return;
		}

		MainThread.BeginInvokeOnMainThread(() =>
		{
			VoiceChannelMemberInfo? info;
			lock (_lock)
			{
				_channelMembers.TryGetValue(member.UserId, out info);
				_channelMembers.Remove(member.UserId);
			}

			if (info is not null)
			{
				MemberLeft?.Invoke(this, info);
			}
		});
	}

	private void OnAudioFrameReceived(EventContext ctx, AudioFrameEvent frame)
	{
		if (_conn is null)
		{
			return;
		}

		// Check if this frame is for our channel
		if (!_currentChannelId.HasValue || frame.ChannelId != _currentChannelId.Value)
		{
			return;
		}

		// Play the audio
		var audioFrame = new Audio.AudioFrame
		{
			Sequence = frame.Sequence,
			SampleRate = frame.SampleRate,
			Channels = frame.Channels,
			Rms = frame.Rms,
			OpusData = [.. frame.OpusData]
		};

		_audioService.PlayAudioFrame(frame.FromUser.ToString(), audioFrame);

		// Notify UI about speaking activity
		if (frame.Rms > _audioService.Settings.MicSensitivity)
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				UserSpeaking?.Invoke(this, (frame.FromUser, frame.Rms));
			});
		}
	}

	private void OnUserVolumeSettingsChanged(EventContext ctx, UserVolumeSettings settings)
	{
		// Apply volume settings to audio service
		_audioService.SetUserVolume(settings.TargetUserId.ToString(), settings.Volume);
	}

	private void OnUserVolumeSettingsUpdated(EventContext ctx, UserVolumeSettings old, UserVolumeSettings settings)
	{
		_audioService.SetUserVolume(settings.TargetUserId.ToString(), settings.Volume);
	}

	private void OnAudioFrameReady(object? sender, Audio.AudioFrame frame)
	{
		if (_conn is null || !_currentChannelId.HasValue)
		{
			return;
		}

		// Send audio frame to SpacetimeDB
		_conn.Reducers.SendAudioFrame(
			frame.Sequence,
			frame.SampleRate,
			frame.Channels,
			frame.Rms,
			[.. frame.OpusData]);
	}

	private void OnMicLevelChanged(object? sender, float level)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			MicLevelChanged?.Invoke(this, level);
		});
	}

	private VoiceChannelMemberInfo CreateMemberInfo(VoiceChannelMember member)
	{
		// Try to get user info
		string? userName = null;
		if (_conn?.Db.User.Identity.Find(member.UserId) is { } user)
		{
			userName = user.Name;
		}

		return new VoiceChannelMemberInfo
		{
			UserId = member.UserId,
			UserName = userName ?? ShortId(member.UserId),
			IsMuted = member.IsMuted,
			IsDeafened = member.IsDeafened,
			JoinedAt = member.JoinedAt
		};
	}

	private static string ShortId(Identity identity)
	{
		var full = identity.ToString();
		return full.Length <= 10 ? full : $"{full[..6]}...{full[^4..]}";
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;

		// Unregister event handlers to prevent leaks
		if (_conn is not null)
		{
			_conn.Db.VoiceChannelMember.OnInsert -= OnVoiceChannelMemberInserted;
			_conn.Db.VoiceChannelMember.OnUpdate -= OnVoiceChannelMemberUpdated;
			_conn.Db.VoiceChannelMember.OnDelete -= OnVoiceChannelMemberDeleted;
			_conn.Db.AudioFrameEvent.OnInsert -= OnAudioFrameReceived;
			_conn.Db.UserVolumeSettings.OnInsert -= OnUserVolumeSettingsChanged;
			_conn.Db.UserVolumeSettings.OnUpdate -= OnUserVolumeSettingsUpdated;
			_conn.Reducers.OnJoinVoiceChannel -= OnJoinVoiceChannelResult;
			_conn.Reducers.OnLeaveVoiceChannel -= OnLeaveVoiceChannelResult;
			_conn.Reducers.OnSetVoiceMuted -= OnSetVoiceMutedResult;
			_conn.Reducers.OnSetVoiceDeafened -= OnSetVoiceDeafenedResult;
			_conn = null;
		}

		_audioService.StopCapture();
		_audioService.Dispose();
	}
}

/// <summary>
/// Information about a voice channel member.
/// </summary>
public sealed class VoiceChannelMemberInfo
{
	public Identity UserId { get; init; }
	public string UserName { get; set; } = "";
	public bool IsMuted { get; set; }
	public bool IsDeafened { get; set; }
	public Timestamp JoinedAt { get; init; }
	public bool IsSpeaking { get; set; }
}
