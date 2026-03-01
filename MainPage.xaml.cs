using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Supernova;

public partial class MainPage : ContentPage, INotifyPropertyChanged
{
	private const string OidcTokenPreferenceKey = "supernova_oidc_token";
	private const string DefaultServerUri = "http://127.0.0.1:3000";
	private const string DefaultDatabaseName = "supernova";
	private const string DefaultAuth0Scope = "openid profile email";
	private const string DefaultAuth0RedirectUri = "supernova://auth";

	private DbConnection? _conn;
	private IDispatcherTimer? _frameTimer;
	private string? _oidcJwt;
	private bool _configurationLoaded;
	private string _serverUri = DefaultServerUri;
	private string _databaseName = DefaultDatabaseName;
	private bool _useDeviceCodeOnWindows = true;
	private OidcConfig? _oidcConfig;
	private readonly OidcAuthService _oidcAuthService = new();

	private readonly Dictionary<Identity, User> _usersByIdentity = new();

	public new event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<UserListItem> Users { get; } = new();
	public ObservableCollection<ChatMessageItem> Messages { get; } = new();

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

		SetAuthenticationToken(Preferences.Get(OidcTokenPreferenceKey, string.Empty));
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
	}

	private void Disconnect()
	{
		if (_conn is null)
		{
			return;
		}

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
		MainThread.BeginInvokeOnMainThread(() =>
		{
			StatusText = $"Connected as {ShortId(identity)}";
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
			if (ex.Message.Contains("auth", StringComparison.OrdinalIgnoreCase)
				|| ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase)
				|| ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
			{
				Disconnect();
			}
		});
	}

	private void OnSubscriptionApplied(SubscriptionEventContext ctx)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_usersByIdentity.Clear();
			Users.Clear();
			Messages.Clear();

			foreach (var user in ctx.Db.User.Iter())
			{
				UpsertUser(user);
			}

			foreach (var message in ctx.Db.Message.Iter().OrderBy(x => x.Sent.MicrosecondsSinceUnixEpoch))
			{
				AddMessage(message);
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

			RefreshMessageSenderNames();
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

	private void UpsertUser(User user)
	{
		_usersByIdentity[user.Identity] = user;

		var displayName = ResolveDisplayName(user.Identity);
		var existing = Users.FirstOrDefault(u => u.Identity == user.Identity);
		if (existing is null)
		{
			Users.Add(new UserListItem(user.Identity, displayName, user.Online));
		}
		else
		{
			existing.DisplayName = displayName;
			existing.Online = user.Online;
		}

		SortUsers();
		RefreshMessageSenderNames();
	}

	private void SortUsers()
	{
		var sorted = Users
			.OrderByDescending(x => x.Online)
			.ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		Users.Clear();
		foreach (var item in sorted)
		{
			Users.Add(item);
		}
	}

	private void AddMessage(Message message)
	{
		Messages.Add(new ChatMessageItem(
			message.Sender,
			ResolveDisplayName(message.Sender),
			message.Text,
			ToLocalDateTime(message.Sent)));
	}

	private void RefreshMessageSenderNames()
	{
		foreach (var message in Messages)
		{
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
		Preferences.Remove(OidcTokenPreferenceKey);
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

			Preferences.Set(OidcTokenPreferenceKey, jwt);
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
		LogText = string.IsNullOrWhiteSpace(LogText)
			? line
			: $"{LogText}{Environment.NewLine}{line}";
	}

	private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
