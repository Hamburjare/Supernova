using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Supernova.Services;

public class SpacetimeService : IDisposable
{
    private DbConnection? _conn;
    private IDispatcherTimer? _tickTimer;

    public Identity? LocalIdentity { get; private set; } = null;
    public string? AuthToken { get; private set; }
    public bool IsConnected { get; private set; }
    public string ConnectionStatus { get; private set; } = "Disconnected";

    public event Action? OnConnectionChanged;
    public event Action? OnUsersChanged;
    public event Action? OnMessagesChanged;
    public event Action<CallSession>? OnCallSessionInserted;
    public event Action<CallSession, CallSession>? OnCallSessionUpdated;
    public event Action<CallSession>? OnCallSessionDeleted;
    public event Action<AudioFrameEvent>? OnAudioFrameReceived;
    public event Action<VideoFrameEvent>? OnVideoFrameReceived;

    public DbConnection? Connection => _conn;

    public async Task ConnectAsync(IDispatcher dispatcher)
    {
        await EnvConfig.LoadAsync();
        string uri = EnvConfig.GetServerUri();
        string database = EnvConfig.GetDatabase();

        string? savedToken = Preferences.Default.Get<string?>("spacetimedb_token", null);

        ConnectionStatus = "Connecting...";
        OnConnectionChanged?.Invoke();

        _conn = DbConnection.Builder()
            .WithUri(uri)
            .WithDatabaseName(database)
            .WithToken(savedToken)
            .OnConnect(OnConnected)
            .OnDisconnect(OnDisconnected)
            .OnConnectError(OnConnectError)
            .Build();

        // Table callbacks
        _conn.Db.User.OnInsert += (ctx, row) => OnUsersChanged?.Invoke();
        _conn.Db.User.OnUpdate += (ctx, oldRow, newRow) => OnUsersChanged?.Invoke();
        _conn.Db.User.OnDelete += (ctx, row) => OnUsersChanged?.Invoke();

        _conn.Db.ChatMessage.OnInsert += (ctx, row) => OnMessagesChanged?.Invoke();
        _conn.Db.ChatMessage.OnDelete += (ctx, row) => OnMessagesChanged?.Invoke();

        _conn.Db.CallSession.OnInsert += (ctx, row) => OnCallSessionInserted?.Invoke(row);
        _conn.Db.CallSession.OnUpdate += (ctx, oldRow, newRow) => OnCallSessionUpdated?.Invoke(oldRow, newRow);
        _conn.Db.CallSession.OnDelete += (ctx, row) => OnCallSessionDeleted?.Invoke(row);

        _conn.Db.AudioFrameEvent.OnInsert += (ctx, row) => OnAudioFrameReceived?.Invoke(row);
        _conn.Db.VideoFrameEvent.OnInsert += (ctx, row) => OnVideoFrameReceived?.Invoke(row);

        // FrameTick on UI thread
        _tickTimer = dispatcher.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromMilliseconds(16);
        _tickTimer.Tick += (s, e) => _conn?.FrameTick();
        _tickTimer.Start();
    }

    private void OnConnected(DbConnection conn, Identity identity, string token)
    {
        LocalIdentity = identity;
        AuthToken = token;
        IsConnected = true;
        ConnectionStatus = "Connected";

        Preferences.Default.Set("spacetimedb_token", token);

        conn.SubscriptionBuilder()
            .OnApplied(OnSubscriptionApplied)
            .OnError((ctx, ex) =>
            {
                ConnectionStatus = $"Subscription error: {ex.Message}";
                OnConnectionChanged?.Invoke();
            })
            .SubscribeToAllTables();

        OnConnectionChanged?.Invoke();
    }

    private void OnSubscriptionApplied(SubscriptionEventContext ctx)
    {
        ConnectionStatus = "Connected & Subscribed";
        OnConnectionChanged?.Invoke();
        OnUsersChanged?.Invoke();
        OnMessagesChanged?.Invoke();
    }

    private void OnDisconnected(DbConnection conn, Exception? error)
    {
        IsConnected = false;
        ConnectionStatus = error != null ? $"Disconnected: {error.Message}" : "Disconnected";
        OnConnectionChanged?.Invoke();
    }

    private void OnConnectError(Exception error)
    {
        IsConnected = false;
        ConnectionStatus = $"Connection error: {error.Message}";
        OnConnectionChanged?.Invoke();
    }

    // --- Reducer wrappers ---

    public void SendMessage(string text) => _conn?.Reducers.SendMessage(text);
    public void SetNickname(string nickname) => _conn?.Reducers.SetNickname(nickname);
    public void RequestCall(Identity target, CallType callType) => _conn?.Reducers.RequestCall(target, callType);
    public void AcceptCall(SpacetimeDB.Uuid sessionId) => _conn?.Reducers.AcceptCall(sessionId);
    public void DeclineCall(SpacetimeDB.Uuid sessionId) => _conn?.Reducers.DeclineCall(sessionId);
    public void EndCall(SpacetimeDB.Uuid sessionId) => _conn?.Reducers.EndCall(sessionId);

    public void SendAudioFrame(SpacetimeDB.Uuid sessionId, Identity to, uint seq,
        uint sampleRate, byte channels, float rms, List<byte> pcm16le)
        => _conn?.Reducers.SendAudioFrame(sessionId, to, seq, sampleRate, channels, rms, pcm16le);

    public void SendVideoFrame(SpacetimeDB.Uuid sessionId, Identity to, uint seq,
        ushort width, ushort height, List<byte> jpeg)
        => _conn?.Reducers.SendVideoFrame(sessionId, to, seq, width, height, jpeg);

    // --- Data access ---

    public IEnumerable<User> GetUsers() =>
        _conn?.Db.User.Iter() ?? Enumerable.Empty<User>();

    public IEnumerable<ChatMessage> GetMessages() =>
        _conn?.Db.ChatMessage.Iter() ?? Enumerable.Empty<ChatMessage>();

    public IEnumerable<CallSession> GetCallSessions() =>
        _conn?.Db.CallSession.Iter() ?? Enumerable.Empty<CallSession>();

    public MediaSettings? GetMediaSettings() =>
        _conn?.Db.MediaSettings.Id.Find(1);

    public User? GetLocalUser()
    {
        if (_conn == null || !LocalIdentity.HasValue) return null;
        return _conn.Db.User.Identity.Find(LocalIdentity.Value);
    }

    public void Dispose()
    {
        _tickTimer?.Stop();
        if (_conn?.IsActive == true)
            _conn.Disconnect();
    }
}
