using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using SpacetimeDB;
using SpacetimeDB.Types;
using Supernova.Audio;

namespace Supernova;

public partial class MainPage : ContentPage, INotifyPropertyChanged
{
	private const string OidcTokenPreferenceKey = "supernova_oidc_token";
	private const string DefaultServerUri = "http://127.0.0.1:3000";
	private const string DefaultDatabaseName = "supernova";
	private const string DefaultAuth0Scope = "openid profile email";
	private const string DefaultAuth0RedirectUri = "supernova://auth";
	private const int MaxLogLines = 500;

	private DbConnection? _conn;
	private IDispatcherTimer? _frameTimer;
	private string? _oidcJwt;
	private bool _configurationLoaded;
	private string _serverUri = DefaultServerUri;
	private string _databaseName = DefaultDatabaseName;
	private bool _useDeviceCodeOnWindows = true;
	private OidcConfig? _oidcConfig;
	private readonly OidcAuthService _oidcAuthService = new();
	private VoiceChannelService? _voiceService;
	private Identity? _localIdentity;
	private CancellationTokenSource? _speakingResetCts;
	private CancellationTokenSource? _saveSettingsCts;

	private readonly Dictionary<Identity, User> _usersByIdentity = new();
	private readonly Dictionary<uint, VoiceChannelListItem> _voiceChannelsById = new();

	public ObservableCollection<UserListItem> Users { get; } = new();
	public ObservableCollection<ChatMessageItem> Messages { get; } = new();
	public ObservableCollection<VoiceChannelListItem> VoiceChannels { get; } = new();

	private string _authStateText = "Not signed in";
	public string AuthStateText
	{
		get => _authStateText;
		set
		{
			if (_authStateText == value)
			{
				return;
			}

			_authStateText = value;
			RaisePropertyChanged();
		}
	}

	public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_oidcJwt);
	public bool IsLoginViewVisible => !IsAuthenticated;
	public bool IsChatViewVisible => IsAuthenticated;

	private bool _isSettingsVisible;
	public bool IsSettingsVisible
	{
		get => _isSettingsVisible;
		set
		{
			if (_isSettingsVisible == value)
			{
				return;
			}

			_isSettingsVisible = value;
			RaisePropertyChanged();
		}
	}

	// Voice Channel Properties
	public bool IsInVoiceChannel => _voiceService?.IsInChannel ?? false;

	private float _micLevel;
	public float MicLevel
	{
		get => _micLevel;
		set
		{
			if (Math.Abs(_micLevel - value) < 0.001f)
			{
				return;
			}

			_micLevel = value;
			RaisePropertyChanged();
			RaisePropertyChanged(nameof(MicLevelColor));
		}
	}

	public Color MicLevelColor => _micLevel > (_voiceService?.AudioService.Settings.MicSensitivity ?? 0.02f)
		? Colors.LimeGreen
		: Colors.Gray;

	public string MuteButtonText => _voiceService?.AudioService.Settings.IsMuted == true ? "Unmute" : "Mute";
	public string DeafenButtonText => _voiceService?.AudioService.Settings.IsDeafened == true ? "Undeafen" : "Deafen";

	// Voice Settings Properties
	private List<AudioDevice> _inputDevices = [];
	public List<AudioDevice> InputDevices
	{
		get => _inputDevices;
		set { _inputDevices = value; RaisePropertyChanged(); }
	}

	private List<AudioDevice> _outputDevices = [];
	public List<AudioDevice> OutputDevices
	{
		get => _outputDevices;
		set { _outputDevices = value; RaisePropertyChanged(); }
	}

	private AudioDevice? _selectedInputDevice;
	public AudioDevice? SelectedInputDevice
	{
		get => _selectedInputDevice;
		set { _selectedInputDevice = value; RaisePropertyChanged(); }
	}

	private AudioDevice? _selectedOutputDevice;
	public AudioDevice? SelectedOutputDevice
	{
		get => _selectedOutputDevice;
		set { _selectedOutputDevice = value; RaisePropertyChanged(); }
	}

	// Input/Output volume as 0-2 range (0-200%)
	private double _inputVolume = 1.0;
	public double InputVolume
	{
		get => _inputVolume;
		set { _inputVolume = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(InputVolumePercent)); }
	}
	public int InputVolumePercent => (int)(_inputVolume * 100);

	private double _outputVolume = 1.0;
	public double OutputVolume
	{
		get => _outputVolume;
		set { _outputVolume = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(OutputVolumePercent)); }
	}
	public int OutputVolumePercent => (int)(_outputVolume * 100);

	// Mic sensitivity (VAD threshold) 0.0 - 0.1
	private double _micSensitivity = 0.02;
	public double MicSensitivity
	{
		get => _micSensitivity;
		set { _micSensitivity = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(MicSensitivityPercent)); }
	}
	public int MicSensitivityPercent => (int)(_micSensitivity * 1000);

	private bool _hearSelf;
	public bool HearSelf
	{
		get => _hearSelf;
		set { _hearSelf = value; RaisePropertyChanged(); }
	}

	private string _statusText = "Disconnected";
	public string StatusText
	{
		get => _statusText;
		set
		{
			if (_statusText == value)
			{
				return;
			}

			_statusText = value;
			RaisePropertyChanged();
			AppendLog(value);
		}
	}

	private string _logText = string.Empty;
	public string LogText
	{
		get => _logText;
		set
		{
			if (_logText == value)
			{
				return;
			}

			_logText = value;
			RaisePropertyChanged();
		}
	}

	private string _pendingName = string.Empty;
	public string PendingName
	{
		get => _pendingName;
		set
		{
			if (_pendingName == value)
			{
				return;
			}

			_pendingName = value;
			RaisePropertyChanged();
		}
	}

	private string _pendingMessage = string.Empty;
	public string PendingMessage
	{
		get => _pendingMessage;
		set
		{
			if (_pendingMessage == value)
			{
				return;
			}

			_pendingMessage = value;
			RaisePropertyChanged();
		}
	}

	public MainPage()
	{
		InitializeComponent();
		BindingContext = this;

		LoadStoredTokenAsync();
	}

	private async void LoadStoredTokenAsync()
	{
		try
		{
			var token = await SecureStorage.Default.GetAsync(OidcTokenPreferenceKey);
			SetAuthenticationToken(token);
		}
		catch
		{
			SetAuthenticationToken(null);
		}
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (!_configurationLoaded)
		{
			await LoadConfigurationAsync();
		}

		StartFrameTimer();

		if (_conn is not null)
		{
			return;
		}

		if (!IsAuthenticated)
		{
			StatusText = "Sign in required";
			return;
		}

		Connect(_oidcJwt!);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();

		StopFrameTimer();
		Disconnect();
	}

	private void Connect()
	{
		if (_oidcJwt is null)
		{
			StatusText = "Sign in required";
			return;
		}

		Connect(_oidcJwt);
	}

	private void Connect(string oidcToken)
	{
		if (_conn is not null)
		{
			return;
		}

		StatusText = "Connecting...";

		_conn = DbConnection.Builder()
			.WithUri(_serverUri)
			.WithDatabaseName(_databaseName)
			.WithToken(oidcToken)
			.OnConnect(OnConnected)
			.OnDisconnect(OnDisconnected)
			.OnConnectError(OnConnectError)
			.Build();

		_conn.OnUnhandledReducerError += (_, ex) =>
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				StatusText = $"Reducer error: {ex.Message}";
			});
		};

		_conn.Reducers.OnSetName += OnSetNameResult;
		_conn.Reducers.OnSendMessage += OnSendMessageResult;
		_conn.Db.User.OnInsert += OnUserInserted;
		_conn.Db.User.OnUpdate += OnUserUpdated;
		_conn.Db.User.OnDelete += OnUserDeleted;
		_conn.Db.Message.OnInsert += OnMessageInserted;

		// Voice channel callbacks
		_conn.Db.VoiceChannel.OnInsert += OnVoiceChannelInserted;
		_conn.Db.VoiceChannel.OnDelete += OnVoiceChannelDeleted;
		_conn.Db.VoiceChannelMember.OnInsert += OnVoiceChannelMemberInserted;
		_conn.Db.VoiceChannelMember.OnUpdate += OnVoiceChannelMemberUpdated;
		_conn.Db.VoiceChannelMember.OnDelete += OnVoiceChannelMemberDeleted;
	}

	private void OnVoiceChannelStateChanged(object? sender, bool isInChannel)
	{
		RaisePropertyChanged(nameof(IsInVoiceChannel));
		RaisePropertyChanged(nameof(MuteButtonText));
		RaisePropertyChanged(nameof(DeafenButtonText));
	}

	private void OnAudioStateChanged(object? sender, EventArgs e)
	{
		RaisePropertyChanged(nameof(MuteButtonText));
		RaisePropertyChanged(nameof(DeafenButtonText));
	}

	private void LoadAudioDevices()
	{
		if (_voiceService is null)
		{
			return;
		}

		InputDevices = [.. _voiceService.AudioService.GetInputDevices()];
		OutputDevices = [.. _voiceService.AudioService.GetOutputDevices()];

		// Select current devices
		var currentInputId = _voiceService.AudioService.Settings.InputDeviceId;
		var currentOutputId = _voiceService.AudioService.Settings.OutputDeviceId;

		SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == currentInputId)
			?? InputDevices.FirstOrDefault(d => d.IsDefault)
			?? InputDevices.FirstOrDefault();

		SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == currentOutputId)
			?? OutputDevices.FirstOrDefault(d => d.IsDefault)
			?? OutputDevices.FirstOrDefault();

		// Load current settings into UI
		InputVolume = _voiceService.AudioService.Settings.MicrophoneVolume / 100.0;
		OutputVolume = _voiceService.AudioService.Settings.OutputVolume / 100.0;
		MicSensitivity = _voiceService.AudioService.Settings.MicSensitivity;
		HearSelf = _voiceService.AudioService.Settings.HearSelf;
	}

	private void Disconnect()
	{
		if (_conn is null)
		{
			return;
		}

		// Clean up voice service
		_voiceService?.Dispose();
		_voiceService = null;

		try
		{
			_conn.Disconnect();
		}
		catch
		{
			// Ignore teardown errors during page close.
		}

		_conn = null;
		StatusText = "Disconnected";

		// Clear voice channels
		VoiceChannels.Clear();
		_voiceChannelsById.Clear();
		RaisePropertyChanged(nameof(IsInVoiceChannel));
	}

	private void StartFrameTimer()
	{
		if (_frameTimer is not null)
		{
			return;
		}

		var dispatcher = Application.Current?.Dispatcher ?? Dispatcher;
		if (dispatcher is null)
		{
			return;
		}

		_frameTimer = dispatcher.CreateTimer();
		_frameTimer.Interval = TimeSpan.FromMilliseconds(16);
		_frameTimer.Tick += (_, _) =>
		{
			_conn?.FrameTick();
		};
		_frameTimer.Start();
	}

	private void StopFrameTimer()
	{
		if (_frameTimer is null)
		{
			return;
		}

		_frameTimer.Stop();
		_frameTimer = null;
	}

	private void OnConnected(DbConnection conn, Identity identity, string token)
	{
		_localIdentity = identity;

		// Initialize voice service now that we have the identity
		_voiceService = new VoiceChannelService();
		_voiceService.Initialize(conn, identity);
		_voiceService.MicLevelChanged += OnVoiceMicLevelChanged;
		_voiceService.UserSpeaking += OnVoiceUserSpeaking;
		_voiceService.ChannelStateChanged += OnVoiceChannelStateChanged;
		_voiceService.AudioStateChanged += OnAudioStateChanged;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			StatusText = $"Connected as {ShortId(identity)}";
			LoadAudioDevices();
		});

		conn.SubscriptionBuilder()
			.OnApplied(OnSubscriptionApplied)
			.OnError((_, ex) =>
			{
				MainThread.BeginInvokeOnMainThread(() =>
				{
					StatusText = $"Subscription error: {ex.Message}";
				});
			})
			.SubscribeToAllTables();
	}

	private void OnDisconnected(DbConnection _, Exception? ex)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			StatusText = ex is null ? "Disconnected" : $"Disconnected: {ex.Message}";
		});
	}

	private void OnConnectError(Exception ex)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			StatusText = $"Connection error: {ex.Message}";
			Disconnect();
		});
	}

	private void OnSubscriptionApplied(SubscriptionEventContext ctx)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_usersByIdentity.Clear();
			Users.Clear();
			Messages.Clear();
			VoiceChannels.Clear();
			_voiceChannelsById.Clear();

			foreach (var user in ctx.Db.User.Iter())
			{
				UpsertUser(user);
			}

			foreach (var message in ctx.Db.Message.Iter().OrderBy(x => x.Sent.MicrosecondsSinceUnixEpoch))
			{
				AddMessage(message);
			}

			// Load voice channels
			foreach (var channel in ctx.Db.VoiceChannel.Iter())
			{
				UpsertVoiceChannel(channel);
			}

			// Load voice channel members
			foreach (var member in ctx.Db.VoiceChannelMember.Iter())
			{
				AddVoiceChannelMember(member);
			}

			// Load user's saved audio settings
			if (_localIdentity.HasValue && _voiceService is not null)
			{
				var savedSettings = ctx.Db.UserAudioSettings.UserId.Find(_localIdentity.Value);
				if (savedSettings is not null)
				{
					ApplySavedAudioSettings(savedSettings);
				}
			}

			StatusText = "Subscribed to messages and users";
			ScrollToLatestMessage();
		});
	}

	private void OnUserInserted(EventContext _, User row)
	{
		MainThread.BeginInvokeOnMainThread(() => UpsertUser(row));
	}

	private void OnUserUpdated(EventContext _, User __, User row)
	{
		MainThread.BeginInvokeOnMainThread(() => UpsertUser(row));
	}

	private void OnUserDeleted(EventContext _, User row)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_usersByIdentity.Remove(row.Identity);
			var existing = Users.FirstOrDefault(u => u.Identity == row.Identity);
			if (existing is not null)
			{
				Users.Remove(existing);
			}

			RefreshMessageSenderNames(row.Identity);
		});
	}

	private void OnMessageInserted(EventContext _, Message row)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			AddMessage(row);
			ScrollToLatestMessage();
		});
	}

	private void OnSetNameResult(ReducerEventContext ctx, string _)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (ctx.Event.Status is Status.Committed)
			{
				StatusText = "Name updated";
				return;
			}

			if (ctx.Event.Status is Status.Failed(var reason))
			{
				StatusText = $"Set name failed: {reason}";
			}
		});
	}

	private void OnSendMessageResult(ReducerEventContext ctx, string _)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (ctx.Event.Status is Status.Committed)
			{
				StatusText = "Message sent";
				return;
			}

			if (ctx.Event.Status is Status.Failed(var reason))
			{
				StatusText = $"Send failed: {reason}";
			}
		});
	}

	// ===========================
	// Voice Channel Callbacks
	// ===========================

	private void OnVoiceChannelInserted(EventContext _, VoiceChannel channel)
	{
		MainThread.BeginInvokeOnMainThread(() => UpsertVoiceChannel(channel));
	}

	private void OnVoiceChannelDeleted(EventContext _, VoiceChannel channel)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (_voiceChannelsById.TryGetValue(channel.Id, out var item))
			{
				VoiceChannels.Remove(item);
				_voiceChannelsById.Remove(channel.Id);
			}
		});
	}

	private void OnVoiceChannelMemberInserted(EventContext _, VoiceChannelMember member)
	{
		MainThread.BeginInvokeOnMainThread(() => AddVoiceChannelMember(member));
	}

	private void OnVoiceChannelMemberUpdated(EventContext _, VoiceChannelMember __, VoiceChannelMember member)
	{
		MainThread.BeginInvokeOnMainThread(() => UpdateVoiceChannelMember(member));
	}

	private void OnVoiceChannelMemberDeleted(EventContext _, VoiceChannelMember member)
	{
		MainThread.BeginInvokeOnMainThread(() => RemoveVoiceChannelMember(member));
	}

	private void OnVoiceMicLevelChanged(object? sender, float level)
	{
		MicLevel = level;
	}

	private void OnVoiceUserSpeaking(object? sender, (Identity User, float Level) args)
	{
		// Find the member in the UI and update speaking state
		foreach (var channel in VoiceChannels)
		{
			var member = channel.Members.FirstOrDefault(m => m.UserId == args.User);
			if (member is not null)
			{
				member.IsSpeaking = args.Level > (_voiceService?.AudioService.Settings.MicSensitivity ?? 0.02f);

				// Cancel any previous reset and schedule a new one
				_speakingResetCts?.Cancel();
				_speakingResetCts = new CancellationTokenSource();
				var token = _speakingResetCts.Token;
				_ = ResetSpeakingAfterDelay(member, token);
				break;
			}
		}
	}

	private static async Task ResetSpeakingAfterDelay(VoiceChannelMemberListItem member, CancellationToken token)
	{
		try
		{
			await Task.Delay(300, token);
			MainThread.BeginInvokeOnMainThread(() => member.IsSpeaking = false);
		}
		catch (TaskCanceledException) { }
	}

	private void UpsertVoiceChannel(VoiceChannel channel)
	{
		if (!_voiceChannelsById.TryGetValue(channel.Id, out var existing))
		{
			existing = new VoiceChannelListItem(channel.Id, channel.Name, channel.MaxMembers);
			VoiceChannels.Add(existing);
			_voiceChannelsById[channel.Id] = existing;
		}

		// Update current channel state
		existing.IsCurrentChannel = _voiceService?.CurrentChannelId == channel.Id;
	}

	private void AddVoiceChannelMember(VoiceChannelMember member)
	{
		if (!_voiceChannelsById.TryGetValue(member.ChannelId, out var channel))
		{
			return;
		}

		// Check if member already exists
		if (channel.Members.Any(m => m.UserId == member.UserId))
		{
			return;
		}

		var userName = ResolveDisplayName(member.UserId);
		var memberItem = new VoiceChannelMemberListItem(member.UserId, userName)
		{
			IsMuted = member.IsMuted,
			IsDeafened = member.IsDeafened
		};
		channel.Members.Add(memberItem);
	}

	private void UpdateVoiceChannelMember(VoiceChannelMember member)
	{
		if (!_voiceChannelsById.TryGetValue(member.ChannelId, out var channel))
		{
			return;
		}

		var existing = channel.Members.FirstOrDefault(m => m.UserId == member.UserId);
		if (existing is not null)
		{
			existing.IsMuted = member.IsMuted;
			existing.IsDeafened = member.IsDeafened;
		}
	}

	private void RemoveVoiceChannelMember(VoiceChannelMember member)
	{
		if (!_voiceChannelsById.TryGetValue(member.ChannelId, out var channel))
		{
			return;
		}

		var existing = channel.Members.FirstOrDefault(m => m.UserId == member.UserId);
		if (existing is not null)
		{
			channel.Members.Remove(existing);
		}
	}

	// ===========================
	// Voice Channel Event Handlers
	// ===========================

	private void OnVoiceChannelJoinClicked(object? sender, EventArgs e)
	{
		if (sender is Button button && button.CommandParameter is uint channelId)
		{
			if (_voiceService?.CurrentChannelId == channelId)
			{
				LeaveVoiceChannel();
			}
			else
			{
				JoinVoiceChannel(channelId);
			}
		}
	}

	private void OnMuteClicked(object? sender, EventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		var newMuted = !_voiceService.AudioService.Settings.IsMuted;
		_voiceService.SetMuted(newMuted);
		RaisePropertyChanged(nameof(MuteButtonText));
	}

	private void OnDeafenClicked(object? sender, EventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		var newDeafened = !_voiceService.AudioService.Settings.IsDeafened;
		_voiceService.SetDeafened(newDeafened);
		RaisePropertyChanged(nameof(DeafenButtonText));
		RaisePropertyChanged(nameof(MuteButtonText));
	}

	private void OnLeaveVoiceChannelClicked(object? sender, EventArgs e)
	{
		LeaveVoiceChannel();
	}

	private void OnInputDeviceChanged(object? sender, EventArgs e)
	{
		if (_voiceService is null || SelectedInputDevice is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.InputDeviceId = SelectedInputDevice.Id;
		// Restart capture if currently capturing to use new device
		if (_voiceService.AudioService.IsCapturing)
		{
			_voiceService.AudioService.StopCapture();
			_voiceService.AudioService.StartCapture();
		}
		SaveAudioSettingsToDb();
	}

	private void OnOutputDeviceChanged(object? sender, EventArgs e)
	{
		if (_voiceService is null || SelectedOutputDevice is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.OutputDeviceId = SelectedOutputDevice.Id;
		SaveAudioSettingsToDb();
	}

	private void OnInputVolumeChanged(object? sender, ValueChangedEventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.MicrophoneVolume = (int)(e.NewValue * 100);
		SaveAudioSettingsToDb();
	}

	private void OnOutputVolumeChanged(object? sender, ValueChangedEventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.OutputVolume = (int)(e.NewValue * 100);
		SaveAudioSettingsToDb();
	}

	private void OnMicSensitivityChanged(object? sender, ValueChangedEventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.MicSensitivity = (float)e.NewValue;
		RaisePropertyChanged(nameof(MicLevelColor));
		SaveAudioSettingsToDb();
	}

	private void OnHearSelfToggled(object? sender, ToggledEventArgs e)
	{
		if (_voiceService is null)
		{
			return;
		}

		_voiceService.AudioService.Settings.HearSelf = e.Value;
		SaveAudioSettingsToDb();
	}

	private void ApplySavedAudioSettings(UserAudioSettings settings)
	{
		if (_voiceService is null)
		{
			return;
		}

		// Apply to audio service
		_voiceService.AudioService.Settings.InputDeviceId = settings.InputDeviceId;
		_voiceService.AudioService.Settings.OutputDeviceId = settings.OutputDeviceId;
		_voiceService.AudioService.Settings.MicrophoneVolume = settings.MicrophoneVolume;
		_voiceService.AudioService.Settings.OutputVolume = settings.OutputVolume;
		_voiceService.AudioService.Settings.MicSensitivity = settings.MicSensitivity / 1000f; // Convert from 0-100 to 0.0-0.1
		_voiceService.AudioService.Settings.HearSelf = settings.HearSelf;

		// Update UI bindings
		InputVolume = settings.MicrophoneVolume / 100.0;
		OutputVolume = settings.OutputVolume / 100.0;
		MicSensitivity = settings.MicSensitivity / 1000.0; // Convert to 0.0-0.1 range
		HearSelf = settings.HearSelf;

		// Select devices in pickers
		SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Id == settings.InputDeviceId)
			?? InputDevices.FirstOrDefault(d => d.IsDefault)
			?? InputDevices.FirstOrDefault();

		SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == settings.OutputDeviceId)
			?? OutputDevices.FirstOrDefault(d => d.IsDefault)
			?? OutputDevices.FirstOrDefault();
	}

	private void SaveAudioSettingsToDb()
	{
		_saveSettingsCts?.Cancel();
		_saveSettingsCts = new CancellationTokenSource();
		var token = _saveSettingsCts.Token;
		_ = SaveAudioSettingsDebounced(token);
	}

	private async Task SaveAudioSettingsDebounced(CancellationToken token)
	{
		try
		{
			await Task.Delay(300, token);
		}
		catch (TaskCanceledException) { return; }

		if (_conn is null || _voiceService is null)
		{
			return;
		}

		var settings = _voiceService.AudioService.Settings;
		_conn.Reducers.SaveAudioSettings(
			settings.InputDeviceId,
			settings.OutputDeviceId,
			(byte)Math.Clamp(settings.MicrophoneVolume, 0, 200),
			(byte)Math.Clamp(settings.OutputVolume, 0, 200),
			(byte)Math.Clamp((int)(settings.MicSensitivity * 1000), 0, 100), // Convert 0.0-0.1 to 0-100
			settings.HearSelf);
	}

	private void JoinVoiceChannel(uint channelId)
	{
		if (_voiceService is null)
		{
			StatusText = "Voice service not available";
			return;
		}

		try
		{
			_voiceService.JoinChannel(channelId);

			// Update UI
			foreach (var channel in VoiceChannels)
			{
				channel.IsCurrentChannel = channel.Id == channelId;
			}

			RaisePropertyChanged(nameof(IsInVoiceChannel));
			StatusText = $"Joining voice channel...";
		}
		catch (Exception ex)
		{
			StatusText = $"Failed to join: {ex.Message}";
		}
	}

	private void LeaveVoiceChannel()
	{
		if (_voiceService is null)
		{
			return;
		}

		_voiceService.LeaveChannel();

		foreach (var channel in VoiceChannels)
		{
			channel.IsCurrentChannel = false;
		}

		RaisePropertyChanged(nameof(IsInVoiceChannel));
		StatusText = "Left voice channel";
	}

	private void UpsertUser(User user)
	{
		var oldName = _usersByIdentity.TryGetValue(user.Identity, out var prev) ? prev.Name : null;
		_usersByIdentity[user.Identity] = user;

		var displayName = ResolveDisplayName(user.Identity);
		var existing = Users.FirstOrDefault(u => u.Identity == user.Identity);
		if (existing is null)
		{
			InsertUserSorted(new UserListItem(user.Identity, displayName, user.Online));
		}
		else
		{
			existing.DisplayName = displayName;
			existing.Online = user.Online;
			ResortUser(existing);
		}

		// Only refresh message names if the user's name actually changed
		if (!string.Equals(oldName, user.Name, StringComparison.Ordinal))
		{
			RefreshMessageSenderNames(user.Identity);
		}
	}

	private void InsertUserSorted(UserListItem item)
	{
		var index = 0;
		for (; index < Users.Count; index++)
		{
			if (CompareUsers(item, Users[index]) < 0)
				break;
		}
		Users.Insert(index, item);
	}

	private void ResortUser(UserListItem item)
	{
		Users.Remove(item);
		InsertUserSorted(item);
	}

	private static int CompareUsers(UserListItem a, UserListItem b)
	{
		var onlineCmp = b.Online.CompareTo(a.Online); // online first
		return onlineCmp != 0 ? onlineCmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
	}

	private void AddMessage(Message message)
	{
		Messages.Add(new ChatMessageItem(
			message.Sender,
			ResolveDisplayName(message.Sender),
			message.Text,
			ToLocalDateTime(message.Sent)));
	}

	private void RefreshMessageSenderNames(Identity? changedIdentity = null)
	{
		foreach (var message in Messages)
		{
			if (changedIdentity.HasValue && message.SenderIdentity != changedIdentity.Value)
				continue;
			message.SenderName = ResolveDisplayName(message.SenderIdentity);
		}
	}

	private void ScrollToLatestMessage()
	{
		if (Messages.Count == 0)
		{
			return;
		}

		MessagesList.ScrollTo(Messages[^1], position: ScrollToPosition.End, animate: true);
	}

	private string ResolveDisplayName(Identity identity)
	{
		if (_usersByIdentity.TryGetValue(identity, out var user) && !string.IsNullOrWhiteSpace(user.Name))
		{
			return user.Name!;
		}

		return ShortId(identity);
	}

	private static DateTime ToLocalDateTime(Timestamp timestamp)
	{
		var millis = timestamp.MicrosecondsSinceUnixEpoch / 1000;
		return DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime;
	}

	private static string ShortId(Identity identity)
	{
		var full = identity.ToString();
		if (full.Length <= 10)
		{
			return full;
		}

		return $"{full[..6]}...{full[^4..]}";
	}

	private void OnSetNameClicked(object? sender, EventArgs e)
	{
		SetName();
	}

	private void OnSetNameCompleted(object? sender, EventArgs e)
	{
		SetName();
	}

	private void SetName()
	{
		if (!IsAuthenticated)
		{
			StatusText = "Sign in required";
			return;
		}

		if (_conn is null)
		{
			StatusText = "Not connected";
			return;
		}

		var name = PendingName.Trim();
		if (string.IsNullOrWhiteSpace(name))
		{
			StatusText = "Name cannot be empty";
			return;
		}

		_conn.Reducers.SetName(name);
	}

	private void OnSendMessageClicked(object? sender, EventArgs e)
	{
		SendMessage();
	}

	private void OnSendMessageCompleted(object? sender, EventArgs e)
	{
		SendMessage();
	}

	private void SendMessage()
	{
		if (!IsAuthenticated)
		{
			StatusText = "Sign in required";
			return;
		}

		if (_conn is null)
		{
			StatusText = "Not connected";
			return;
		}

		var text = PendingMessage.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		_conn.Reducers.SendMessage(text);
		PendingMessage = string.Empty;
	}

	private async void OnSignInClicked(object? sender, EventArgs e)
	{
		await SignInAsync();
	}

	private void OnSignOutClicked(object? sender, EventArgs e)
	{
		Disconnect();
		SecureStorage.Default.Remove(OidcTokenPreferenceKey);
		SetAuthenticationToken(null);
		StatusText = "Signed out";
	}

	private void OnToggleSettingsClicked(object? sender, EventArgs e)
	{
		if (!IsAuthenticated)
		{
			return;
		}

		IsSettingsVisible = !IsSettingsVisible;
	}

	private async Task SignInAsync()
	{
		if (_oidcConfig is null)
		{
			StatusText = "OIDC is not configured. Check .env values.";
			return;
		}

		if (_conn is not null)
		{
			Disconnect();
		}

		StatusText = $"Signing in via {_oidcConfig.Authority}...";

		try
		{
			OidcTokens tokens;
			if (DeviceInfo.Platform == DevicePlatform.WinUI)
			{
				if (!_useDeviceCodeOnWindows)
				{
					StatusText = "Windows requires Device Code auth. Set AUTH0_USE_DEVICE_CODE=true in .env.";
					return;
				}

				try
				{
					tokens = await _oidcAuthService.SignInWithAuth0DeviceCodeAsync(
						_oidcConfig,
						status => MainThread.BeginInvokeOnMainThread(() => StatusText = status));
				}
				catch (InvalidOperationException ex) when (ex.Message.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
					|| ex.Message.Contains("grant type", StringComparison.OrdinalIgnoreCase)
					|| ex.Message.Contains("device_code", StringComparison.OrdinalIgnoreCase))
				{
					StatusText = "Auth0 rejected Device Code. Enable 'Device Code' grant for this Auth0 application.";
					return;
				}
			}
			else
			{
				tokens = await _oidcAuthService.SignInAsync(_oidcConfig);
			}
			var jwt = SelectSpacetimeToken(tokens);

			if (string.IsNullOrWhiteSpace(jwt))
			{
				throw new InvalidOperationException("OIDC provider did not return a JWT token usable by SpacetimeDB.");
			}

			await SecureStorage.Default.SetAsync(OidcTokenPreferenceKey, jwt);
			SetAuthenticationToken(jwt);
			StatusText = "Sign in successful";
			Connect(jwt);
		}
		catch (OperationCanceledException)
		{
			StatusText = "Sign in canceled";
		}
		catch (Exception ex)
		{
			if (ex.Message.Contains("Unknown host", StringComparison.OrdinalIgnoreCase)
				|| ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
				|| ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
			{
				StatusText = $"Sign in failed: cannot resolve Auth0 host '{_oidcConfig.Authority}'. Rebuild and restart the app after editing .env.";
				return;
			}

			StatusText = $"Sign in failed: {ex.Message}";
		}
	}

	private async void OnCopyLogClicked(object? sender, EventArgs e)
	{
		await Clipboard.Default.SetTextAsync(LogText ?? string.Empty);
		StatusText = "Log copied to clipboard";
	}

	private void OnClearLogClicked(object? sender, EventArgs e)
	{
		LogText = string.Empty;
		StatusText = "Log cleared";
	}

	private async Task LoadConfigurationAsync()
	{
		var env = await EnvConfiguration.LoadAsync();

		_serverUri = env.GetOrDefault("SPACETIMEDB_SERVER_URI", DefaultServerUri);
		_databaseName = env.GetOrDefault("SPACETIMEDB_DATABASE", DefaultDatabaseName);

		var authority = env.Get("AUTH0_DOMAIN");
		var clientId = env.Get("AUTH0_CLIENT_ID");
		var scope = env.GetOrDefault("AUTH0_SCOPE", DefaultAuth0Scope);
		var redirectUri = env.GetOrDefault("AUTH0_REDIRECT_URI", DefaultAuth0RedirectUri);
		var audience = env.Get("AUTH0_AUDIENCE");
		var useDeviceCodeRaw = env.GetOrDefault("AUTH0_USE_DEVICE_CODE", "true");
		_useDeviceCodeOnWindows = !string.Equals(useDeviceCodeRaw, "false", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(useDeviceCodeRaw, "0", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(useDeviceCodeRaw, "no", StringComparison.OrdinalIgnoreCase);

		if (!string.IsNullOrWhiteSpace(authority)
			&& !authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			&& !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			authority = $"https://{authority}";
		}

		if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId))
		{
			_oidcConfig = new OidcConfig(authority!, clientId!, scope, redirectUri, audience);
			_configurationLoaded = true;
			return;
		}

		StatusText = "Missing AUTH0_DOMAIN or AUTH0_CLIENT_ID in .env";
		_configurationLoaded = true;
	}

	private void SetAuthenticationToken(string? jwt)
	{
		_oidcJwt = string.IsNullOrWhiteSpace(jwt) ? null : jwt;
		AuthStateText = _oidcJwt is null ? "Not signed in" : $"Signed in as {DescribeJwt(_oidcJwt)}";
		RaisePropertyChanged(nameof(IsAuthenticated));
		RaisePropertyChanged(nameof(IsLoginViewVisible));
		RaisePropertyChanged(nameof(IsChatViewVisible));

		if (!IsAuthenticated)
		{
			IsSettingsVisible = false;
		}
	}

	private static string SelectSpacetimeToken(OidcTokens tokens)
	{
		if (IsJwt(tokens.IdToken))
		{
			return tokens.IdToken!;
		}

		if (IsJwt(tokens.AccessToken))
		{
			return tokens.AccessToken!;
		}

		return string.Empty;
	}

	private static bool IsJwt(string? token)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return false;
		}

		var segments = token.Split('.');
		return segments.Length == 3;
	}

	private static string DescribeJwt(string jwt)
	{
		try
		{
			var payload = jwt.Split('.')[1];
			var json = DecodeBase64Url(payload);
			using var doc = System.Text.Json.JsonDocument.Parse(json);
			if (doc.RootElement.TryGetProperty("email", out var email))
			{
				return email.GetString() ?? "authenticated user";
			}

			if (doc.RootElement.TryGetProperty("name", out var name))
			{
				return name.GetString() ?? "authenticated user";
			}

			if (doc.RootElement.TryGetProperty("sub", out var sub))
			{
				var value = sub.GetString();
				if (!string.IsNullOrWhiteSpace(value))
				{
					return value.Length <= 14 ? value : $"{value[..10]}...";
				}
			}
		}
		catch
		{
		}

		return "authenticated user";
	}

	private static string DecodeBase64Url(string value)
	{
		var base64 = value.Replace('-', '+').Replace('_', '/');
		switch (base64.Length % 4)
		{
			case 2:
				base64 += "==";
				break;
			case 3:
				base64 += "=";
				break;
		}

		return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
	}

	private void AppendLog(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
		if (string.IsNullOrWhiteSpace(LogText))
		{
			LogText = line;
		}
		else
		{
			var combined = $"{LogText}{Environment.NewLine}{line}";
			// Cap the log to prevent unbounded memory growth
			var lines = combined.Split(Environment.NewLine);
			if (lines.Length > MaxLogLines)
			{
				LogText = string.Join(Environment.NewLine, lines[^MaxLogLines..]);
			}
			else
			{
				LogText = combined;
			}
		}
	}

	private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
	{
		OnPropertyChanged(propertyName);
	}
}

public sealed class UserListItem : INotifyPropertyChanged
{
	private string _displayName;
	private bool _online;

	public event PropertyChangedEventHandler? PropertyChanged;

	public Identity Identity { get; }

	public string DisplayName
	{
		get => _displayName;
		set
		{
			if (_displayName == value)
			{
				return;
			}

			_displayName = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
		}
	}

	public bool Online
	{
		get => _online;
		set
		{
			if (_online == value)
			{
				return;
			}

			_online = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Online)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceIcon)));
		}
	}

	public string PresenceIcon => Online ? "●" : "○";

	public UserListItem(Identity identity, string displayName, bool online)
	{
		Identity = identity;
		_displayName = displayName;
		_online = online;
	}
}

public sealed class ChatMessageItem : INotifyPropertyChanged
{
	private string _senderName;

	public event PropertyChangedEventHandler? PropertyChanged;

	public Identity SenderIdentity { get; }
	public string Text { get; }
	public DateTime SentAt { get; }

	public string SentLocalTime => SentAt.ToString("HH:mm");

	public string SenderName
	{
		get => _senderName;
		set
		{
			if (_senderName == value)
			{
				return;
			}

			_senderName = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SenderName)));
		}
	}

	public ChatMessageItem(Identity senderIdentity, string senderName, string text, DateTime sentAt)
	{
		SenderIdentity = senderIdentity;
		_senderName = senderName;
		Text = text;
		SentAt = sentAt;
	}
}

/// <summary>
/// View model for voice channel list items.
/// </summary>
public sealed class VoiceChannelListItem : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	public uint Id { get; }
	public string Name { get; }
	public uint MaxMembers { get; }

	private bool _isCurrentChannel;
	public bool IsCurrentChannel
	{
		get => _isCurrentChannel;
		set
		{
			if (_isCurrentChannel == value) return;
			_isCurrentChannel = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentChannel)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JoinButtonText)));
		}
	}

	public string JoinButtonText => IsCurrentChannel ? "Leave" : "Join";

	public ObservableCollection<VoiceChannelMemberListItem> Members { get; } = new();

	public VoiceChannelListItem(uint id, string name, uint maxMembers)
	{
		Id = id;
		Name = name;
		MaxMembers = maxMembers;
	}
}

/// <summary>
/// View model for voice channel member list items.
/// </summary>
public sealed class VoiceChannelMemberListItem : INotifyPropertyChanged
{
	public event PropertyChangedEventHandler? PropertyChanged;

	public Identity UserId { get; }

	private string _userName;
	public string UserName
	{
		get => _userName;
		set
		{
			if (_userName == value) return;
			_userName = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UserName)));
		}
	}

	private bool _isMuted;
	public bool IsMuted
	{
		get => _isMuted;
		set
		{
			if (_isMuted == value) return;
			_isMuted = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMuted)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusIcon)));
		}
	}

	private bool _isDeafened;
	public bool IsDeafened
	{
		get => _isDeafened;
		set
		{
			if (_isDeafened == value) return;
			_isDeafened = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeafened)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusIcon)));
		}
	}

	private bool _isSpeaking;
	public bool IsSpeaking
	{
		get => _isSpeaking;
		set
		{
			if (_isSpeaking == value) return;
			_isSpeaking = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingIndicatorColor)));
		}
	}

	public string StatusIcon => IsDeafened ? "🔇" : IsMuted ? "🔇" : "🎤";
	public Color SpeakingIndicatorColor => IsSpeaking ? Colors.LimeGreen : Colors.Transparent;

	public VoiceChannelMemberListItem(Identity userId, string userName)
	{
		UserId = userId;
		_userName = userName;
	}
}
