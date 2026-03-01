using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Supernova;

public partial class MainPage : ContentPage, INotifyPropertyChanged
{
	private const string DefaultServerUri = "https://spacetime.glitchy.rocks";
	private const string ModuleName = "supernova";
	private const string AuthTokenPreferenceKey = "supernova_auth_token";

	private DbConnection? _conn;
	private IDispatcherTimer? _frameTimer;
	private bool _connectStarted;

	private readonly Dictionary<Identity, User> _usersByIdentity = new();

	public new event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<UserListItem> Users { get; } = new();
	public ObservableCollection<ChatMessageItem> Messages { get; } = new();

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
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		StartFrameTimer();

		if (_connectStarted)
		{
			return;
		}

		_connectStarted = true;
		Connect();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();

		StopFrameTimer();
		Disconnect();
	}

	private void Connect()
	{
		if (_conn is not null)
		{
			return;
		}

		StatusText = "Connecting...";

		var savedToken = Preferences.Get(AuthTokenPreferenceKey, string.Empty);

		_conn = DbConnection.Builder()
			.WithUri(DefaultServerUri)
			.WithDatabaseName(ModuleName)
			.WithToken(string.IsNullOrWhiteSpace(savedToken) ? null : savedToken)
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
		_connectStarted = false;
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
		Preferences.Set(AuthTokenPreferenceKey, token);

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
