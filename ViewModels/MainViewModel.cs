using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace Supernova.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Services.SpacetimeService _stdb;
    private readonly Services.AudioService _audio;

    public ObservableCollection<UserDisplay> Users { get; } = new();
    public ObservableCollection<MessageDisplay> Messages { get; } = new();

    // --- Connection ---
    private string _connectionStatus = "Disconnected";
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    private Color _connectionColor = Colors.Red;
    public Color ConnectionColor { get => _connectionColor; set => Set(ref _connectionColor, value); }

    // --- Local user ---
    private string _nickname = "";
    public string Nickname { get => _nickname; set => Set(ref _nickname, value); }

    private string _nicknameEntry = "";
    public string NicknameEntry { get => _nicknameEntry; set => Set(ref _nicknameEntry, value); }

    // --- Chat ---
    private string _messageText = "";
    public string MessageText { get => _messageText; set => Set(ref _messageText, value); }

    // --- Call state ---
    private bool _isInCall;
    public bool IsInCall { get => _isInCall; set => Set(ref _isInCall, value); }

    private bool _isRinging;
    public bool IsRinging { get => _isRinging; set => Set(ref _isRinging, value); }

    private bool _isIncomingCall;
    public bool IsIncomingCall { get => _isIncomingCall; set => Set(ref _isIncomingCall, value); }

    private string _callStatusText = "";
    public string CallStatusText { get => _callStatusText; set => Set(ref _callStatusText, value); }

    private string _callPeerName = "";
    public string CallPeerName { get => _callPeerName; set => Set(ref _callPeerName, value); }

    private bool _isVideoCall;
    public bool IsVideoCall { get => _isVideoCall; set => Set(ref _isVideoCall, value); }

    private ImageSource? _remoteVideoFrame;
    public ImageSource? RemoteVideoFrame { get => _remoteVideoFrame; set => Set(ref _remoteVideoFrame, value); }

    private CallSession? _activeCallSession;

    // --- Commands ---
    public ICommand SendMessageCommand { get; }
    public ICommand ChangeNameCommand { get; }
    public ICommand VoiceCallCommand { get; }
    public ICommand VideoCallCommand { get; }
    public ICommand AcceptCallCommand { get; }
    public ICommand DeclineCallCommand { get; }
    public ICommand EndCallCommand { get; }

    public MainViewModel(Services.SpacetimeService stdb, Services.AudioService audio)
    {
        _stdb = stdb;
        _audio = audio;

        SendMessageCommand = new Command(DoSendMessage);
        ChangeNameCommand = new Command(DoChangeName);
        VoiceCallCommand = new Command<UserDisplay>(DoVoiceCall);
        VideoCallCommand = new Command<UserDisplay>(DoVideoCall);
        AcceptCallCommand = new Command(DoAcceptCall);
        DeclineCallCommand = new Command(DoDeclineCall);
        EndCallCommand = new Command(DoEndCall);

        _stdb.OnConnectionChanged += RefreshConnectionStatus;
        _stdb.OnUsersChanged += RefreshUsers;
        _stdb.OnMessagesChanged += RefreshMessages;
        _stdb.OnCallSessionInserted += OnCallInserted;
        _stdb.OnCallSessionUpdated += OnCallUpdated;
        _stdb.OnCallSessionDeleted += OnCallDeleted;
        _stdb.OnAudioFrameReceived += OnAudioFrame;
        _stdb.OnVideoFrameReceived += OnVideoFrame;

        _audio.OnFrameCaptured += OnLocalAudioCaptured;
    }

    public async Task InitializeAsync(IDispatcher dispatcher)
    {
        await _stdb.ConnectAsync(dispatcher);
    }

    // --- Refresh helpers (called on UI thread via FrameTick) ---

    private void RefreshConnectionStatus()
    {
        ConnectionStatus = _stdb.ConnectionStatus;
        ConnectionColor = _stdb.IsConnected ? Colors.LimeGreen : Colors.Red;

        var localUser = _stdb.GetLocalUser();
        if (localUser != null)
            Nickname = localUser.Nickname;
    }

    private void RefreshUsers()
    {
        Users.Clear();
        foreach (var u in _stdb.GetUsers())
        {
            if (_stdb.LocalIdentity != null && u.Identity.Equals(_stdb.LocalIdentity))
                continue; // skip self
            Users.Add(new UserDisplay(u.Identity, u.Nickname));
        }

        var localUser = _stdb.GetLocalUser();
        if (localUser != null)
            Nickname = localUser.Nickname;
    }

    private void RefreshMessages()
    {
        Messages.Clear();
        var sorted = _stdb.GetMessages()
            .OrderBy(m => m.SentAt.MicrosecondsSinceUnixEpoch);
        foreach (var m in sorted)
        {
            string sender = ResolveNickname(m.Sender);
            bool isMe = _stdb.LocalIdentity != null && m.Sender.Equals(_stdb.LocalIdentity);
            Messages.Add(new MessageDisplay(sender, m.Text, m.SentAt, isMe));
        }
    }

    private string ResolveNickname(Identity identity)
    {
        foreach (var u in _stdb.GetUsers())
        {
            if (u.Identity.Equals(identity))
                return u.Nickname;
        }
        return identity.ToString()[..Math.Min(8, identity.ToString().Length)];
    }

    // --- Commands ---

    private void DoSendMessage()
    {
        if (string.IsNullOrWhiteSpace(MessageText)) return;
        _stdb.SendMessage(MessageText);
        MessageText = "";
    }

    private void DoChangeName()
    {
        if (string.IsNullOrWhiteSpace(NicknameEntry)) return;
        _stdb.SetNickname(NicknameEntry);
        NicknameEntry = "";
    }

    private void DoVoiceCall(UserDisplay? user)
    {
        if (user == null || IsInCall) return;
        _stdb.RequestCall(user.Identity, CallType.Voice);
    }

    private void DoVideoCall(UserDisplay? user)
    {
        if (user == null || IsInCall) return;
        _stdb.RequestCall(user.Identity, CallType.Video);
    }

    private void DoAcceptCall()
    {
        if (_activeCallSession != null)
            _stdb.AcceptCall(_activeCallSession.SessionId);
    }

    private void DoDeclineCall()
    {
        if (_activeCallSession != null)
            _stdb.DeclineCall(_activeCallSession.SessionId);
        ClearCallState();
    }

    private void DoEndCall()
    {
        if (_activeCallSession != null)
            _stdb.EndCall(_activeCallSession.SessionId);
        StopMedia();
        ClearCallState();
    }

    // --- Call session handlers ---

    private void OnCallInserted(CallSession sess)
    {
        if (_stdb.LocalIdentity == null) return;
        bool isCaller = sess.Caller.Equals(_stdb.LocalIdentity);
        bool isCallee = sess.Callee.Equals(_stdb.LocalIdentity);
        if (!isCaller && !isCallee) return;

        _activeCallSession = sess;
        IsInCall = true;
        IsVideoCall = sess.CallType == CallType.Video;

        var peer = isCaller ? sess.Callee : sess.Caller;
        CallPeerName = ResolveNickname(peer);

        if (sess.State == CallState.Ringing)
        {
            IsRinging = true;
            IsIncomingCall = isCallee;
            string kind = IsVideoCall ? "video" : "voice";
            CallStatusText = isCallee
                ? $"Incoming {kind} call from {CallPeerName}"
                : $"Calling {CallPeerName}...";
        }
        else if (sess.State == CallState.Active)
        {
            IsRinging = false;
            CallStatusText = $"In {(IsVideoCall ? "video" : "voice")} call with {CallPeerName}";
            StartMedia();
        }
    }

    private void OnCallUpdated(CallSession oldSess, CallSession newSess)
    {
        if (_stdb.LocalIdentity == null) return;
        bool isCaller = newSess.Caller.Equals(_stdb.LocalIdentity);
        bool isCallee = newSess.Callee.Equals(_stdb.LocalIdentity);
        if (!isCaller && !isCallee) return;

        _activeCallSession = newSess;

        if (newSess.State == CallState.Active)
        {
            IsRinging = false;
            IsIncomingCall = false;
            CallStatusText = $"In {(IsVideoCall ? "video" : "voice")} call with {CallPeerName}";
            StartMedia();
        }
    }

    private void OnCallDeleted(CallSession sess)
    {
        if (_activeCallSession != null &&
            _activeCallSession.SessionId.Equals(sess.SessionId))
        {
            StopMedia();
            ClearCallState();
        }
    }

    // --- Media frame handlers ---

    private void OnAudioFrame(AudioFrameEvent frame)
    {
        if (_stdb.LocalIdentity != null && frame.To.Equals(_stdb.LocalIdentity))
        {
            _audio.PlayFrame(frame.Pcm16Le.ToArray());
        }
    }

    private void OnVideoFrame(VideoFrameEvent frame)
    {
        if (_stdb.LocalIdentity != null && frame.To.Equals(_stdb.LocalIdentity))
        {
            var bytes = frame.Jpeg.ToArray();
            RemoteVideoFrame = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
    }

    private void OnLocalAudioCaptured(uint seq, uint sampleRate, byte channels, float rms, List<byte> pcm16le)
    {
        if (_activeCallSession == null || _activeCallSession.State != CallState.Active) return;
        if (_stdb.LocalIdentity == null) return;

        var peer = _activeCallSession.Caller.Equals(_stdb.LocalIdentity)
            ? _activeCallSession.Callee
            : _activeCallSession.Caller;

        _stdb.SendAudioFrame(_activeCallSession.SessionId, peer, seq, sampleRate, channels, rms, pcm16le);
    }

    // --- Media start/stop ---

    private void StartMedia()
    {
        var settings = _stdb.GetMediaSettings();
        if (settings != null)
            _audio.Configure(settings.AudioTargetSampleRate, settings.AudioFrameMs);

        _audio.StartCapture();
        _audio.StartPlayback();
    }

    private void StopMedia()
    {
        _audio.StopCapture();
        _audio.StopPlayback();
        RemoteVideoFrame = null;
    }

    private void ClearCallState()
    {
        _activeCallSession = null;
        IsInCall = false;
        IsRinging = false;
        IsIncomingCall = false;
        IsVideoCall = false;
        CallStatusText = "";
        CallPeerName = "";
        RemoteVideoFrame = null;
    }

    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public void Dispose()
    {
        _stdb.Dispose();
        _audio.Dispose();
    }
}

// --- Display models (generated types use fields, not bindable properties) ---

public class UserDisplay
{
    public Identity Identity { get; }
    public string Nickname { get; }

    public UserDisplay(Identity identity, string nickname)
    {
        Identity = identity;
        Nickname = nickname;
    }
}

public class MessageDisplay
{
    public string Sender { get; }
    public string Text { get; }
    public string Time { get; }
    public bool IsMe { get; }
    public Color BubbleColor => IsMe ? Color.FromArgb("#512BD4") : Color.FromArgb("#404040");
    public LayoutOptions HorizontalAlign => IsMe ? LayoutOptions.End : LayoutOptions.Start;

    public MessageDisplay(string sender, string text, Timestamp sentAt, bool isMe)
    {
        Sender = sender;
        Text = text;
        IsMe = isMe;
        Time = DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(sentAt.MicrosecondsSinceUnixEpoch / 1000)
        ).LocalDateTime.ToString("HH:mm");
    }
}
