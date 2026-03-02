using SpacetimeDB;

public static partial class Module
{
    private static readonly string[] AllowedIssuers =
    {
        // Example: "https://your-tenant.us.auth0.com/"
    };

    private static readonly string[] AllowedAudiences =
    {
        // Example: "your_auth0_client_id"
    };

    private static void EnsureAuthorized(ReducerContext ctx)
    {
        var jwt = ctx.SenderAuth.Jwt ?? throw new Exception("Unauthorized: JWT is required");

        if (AllowedIssuers.Length > 0 && !AllowedIssuers.Contains(jwt.Issuer, StringComparer.Ordinal))
        {
            throw new Exception($"Unauthorized: invalid issuer {jwt.Issuer}");
        }

        if (AllowedAudiences.Length > 0 && !AllowedAudiences.Any(aud => jwt.Audience.Contains(aud, StringComparer.Ordinal)))
        {
            throw new Exception("Unauthorized: invalid audience");
        }
    }

    // ===========================
    // User Table
    // ===========================

    [Table(Accessor = "User", Public = true)]
    public partial class User
    {
        [PrimaryKey]
        public Identity Identity;
        public string? Name;
        public bool Online;
    }

    // ===========================
    // Message Table
    // ===========================

    [Table(Accessor = "Message", Public = true)]
    public partial class Message
    {
        public Identity Sender;
        public Timestamp Sent;
        public string Text = "";
    }

    // ===========================
    // Voice Channel Tables
    // ===========================

    /// <summary>
    /// Represents a voice channel that users can join.
    /// </summary>
    [Table(Accessor = "VoiceChannel", Public = true)]
    public partial class VoiceChannel
    {
        [PrimaryKey]
        [AutoInc]
        public uint Id;
        public string Name = "";
        public uint MaxMembers;
        public Timestamp CreatedAt;
    }

    /// <summary>
    /// Tracks users who are currently in a voice channel.
    /// Also stores per-user settings like self-mute state.
    /// </summary>
    [Table(Accessor = "VoiceChannelMember", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "ByChannel", Columns = ["ChannelId"])]
    public partial class VoiceChannelMember
    {
        [PrimaryKey]
        public Identity UserId;
        public uint ChannelId;
        public bool IsMuted;
        public bool IsDeafened;
        public Timestamp JoinedAt;
    }

    /// <summary>
    /// Per-user volume settings for other users in voice channels.
    /// Stored server-side so it persists across sessions.
    /// </summary>
    [Table(Accessor = "UserVolumeSettings", Public = true)]
    [SpacetimeDB.Index.BTree(Accessor = "ByUser", Columns = ["UserId"])]
    public partial class UserVolumeSettings
    {
        [PrimaryKey]
        [AutoInc]
        public uint Id;
        public Identity UserId;
        public Identity TargetUserId;
        /// <summary>Volume level 0-200 where 100 is default (1.0x)</summary>
        public byte Volume;
    }

    /// <summary>
    /// Event table for streaming audio frames. Rows are ephemeral - broadcast to 
    /// subscribers then automatically deleted. Not persisted between transactions.
    /// </summary>
    [Table(Accessor = "AudioFrameEvent", Public = true, Event = true)]
    public partial class AudioFrameEvent
    {
        public uint ChannelId;
        public Identity FromUser;
        public uint Sequence;
        /// <summary>Sample rate in Hz (e.g., 16000, 24000, 48000)</summary>
        public uint SampleRate;
        /// <summary>Number of audio channels (1=mono, 2=stereo)</summary>
        public byte Channels;
        /// <summary>RMS level for voice activity detection on receiver side</summary>
        public float Rms;
        /// <summary>Opus-encoded audio data for efficient transmission</summary>
        public byte[] OpusData = [];
    }

    /// <summary>
    /// Persistent audio settings for users. Stored per-user so settings 
    /// are remembered across sessions and devices.
    /// </summary>
    [Table(Accessor = "UserAudioSettings", Public = true)]
    public partial class UserAudioSettings
    {
        [PrimaryKey]
        public Identity UserId;
        /// <summary>Input device ID (null for default)</summary>
        public string? InputDeviceId;
        /// <summary>Output device ID (null for default)</summary>
        public string? OutputDeviceId;
        /// <summary>Microphone volume 0-200 (100 = normal)</summary>
        public byte MicrophoneVolume;
        /// <summary>Output volume 0-200 (100 = normal)</summary>
        public byte OutputVolume;
        /// <summary>Mic sensitivity threshold 0-100 (scaled to 0.0-0.1)</summary>
        public byte MicSensitivity;
        /// <summary>Whether to hear your own voice (loopback)</summary>
        public bool HearSelf;
    }

    // ===========================
    // User Reducers
    // ===========================

    [Reducer]
    public static void SetName(ReducerContext ctx, string name)
    {
        EnsureAuthorized(ctx);
        name = ValidateName(name);

        if (ctx.Db.User.Identity.Find(ctx.Sender) is User user)
        {
            user.Name = name;
            ctx.Db.User.Identity.Update(user);
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new Exception("Names must not be empty");
        }
        return name;
    }

    // ===========================
    // Message Reducers
    // ===========================

    [Reducer]
    public static void SendMessage(ReducerContext ctx, string text)
    {
        EnsureAuthorized(ctx);
        text = ValidateMessage(text);
        Log.Info(text);
        ctx.Db.Message.Insert(
            new Message
            {
                Sender = ctx.Sender,
                Text = text,
                Sent = ctx.Timestamp,
            }
        );
    }

    private static string ValidateMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Messages must not be empty");
        }
        return text;
    }

    // ===========================
    // Voice Channel Reducers
    // ===========================

    /// <summary>
    /// Creates a new voice channel.
    /// </summary>
    [Reducer]
    public static void CreateVoiceChannel(ReducerContext ctx, string name, uint maxMembers)
    {
        EnsureAuthorized(ctx);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new Exception("Channel name cannot be empty");
        }

        if (name.Length > 50)
        {
            throw new Exception("Channel name must be 50 characters or less");
        }

        if (maxMembers == 0)
        {
            maxMembers = 25; // Default max
        }

        if (maxMembers > 100)
        {
            throw new Exception("Max members cannot exceed 100");
        }

        ctx.Db.VoiceChannel.Insert(new VoiceChannel
        {
            Id = 0, // Auto-increment
            Name = name.Trim(),
            MaxMembers = maxMembers,
            CreatedAt = ctx.Timestamp
        });

        Log.Info($"Voice channel '{name}' created by {ctx.Sender}");
    }

    /// <summary>
    /// Deletes a voice channel and removes all members.
    /// </summary>
    [Reducer]
    public static void DeleteVoiceChannel(ReducerContext ctx, uint channelId)
    {
        EnsureAuthorized(ctx);

        if (ctx.Db.VoiceChannel.Id.Find(channelId) is not VoiceChannel channel)
        {
            throw new Exception("Voice channel not found");
        }

        // Remove all members from this channel
        var membersToRemove = ctx.Db.VoiceChannelMember.ByChannel.Filter(channelId).ToList();
        foreach (var member in membersToRemove)
        {
            ctx.Db.VoiceChannelMember.UserId.Delete(member.UserId);
        }

        ctx.Db.VoiceChannel.Id.Delete(channelId);
        Log.Info($"Voice channel '{channel.Name}' (ID: {channelId}) deleted");
    }

    /// <summary>
    /// Joins a voice channel. Automatically leaves any current channel.
    /// </summary>
    [Reducer]
    public static void JoinVoiceChannel(ReducerContext ctx, uint channelId)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (ctx.Db.VoiceChannel.Id.Find(channelId) is not VoiceChannel channel)
        {
            throw new Exception("Voice channel not found");
        }

        // Check if already in this channel
        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is VoiceChannelMember existing)
        {
            if (existing.ChannelId == channelId)
            {
                return; // Already in this channel
            }
            // Leave current channel first
            ctx.Db.VoiceChannelMember.UserId.Delete(sender);
        }

        // Check member limit
        var currentCount = ctx.Db.VoiceChannelMember.ByChannel.Filter(channelId).Count();
        if (currentCount >= channel.MaxMembers)
        {
            throw new Exception("Voice channel is full");
        }

        ctx.Db.VoiceChannelMember.Insert(new VoiceChannelMember
        {
            UserId = sender,
            ChannelId = channelId,
            IsMuted = false,
            IsDeafened = false,
            JoinedAt = ctx.Timestamp
        });

        Log.Info($"User {sender} joined voice channel '{channel.Name}'");
    }

    /// <summary>
    /// Leaves the current voice channel.
    /// </summary>
    [Reducer]
    public static void LeaveVoiceChannel(ReducerContext ctx)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is not VoiceChannelMember member)
        {
            return; // Not in any channel
        }

        ctx.Db.VoiceChannelMember.UserId.Delete(sender);
        Log.Info($"User {sender} left voice channel {member.ChannelId}");
    }

    /// <summary>
    /// Sets the user's mute state in the voice channel.
    /// </summary>
    [Reducer]
    public static void SetVoiceMuted(ReducerContext ctx, bool muted)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is not VoiceChannelMember member)
        {
            throw new Exception("Not in a voice channel");
        }

        member.IsMuted = muted;
        ctx.Db.VoiceChannelMember.UserId.Update(member);
    }

    /// <summary>
    /// Sets the user's deafened state in the voice channel.
    /// </summary>
    [Reducer]
    public static void SetVoiceDeafened(ReducerContext ctx, bool deafened)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is not VoiceChannelMember member)
        {
            throw new Exception("Not in a voice channel");
        }

        member.IsDeafened = deafened;
        // Deafening also mutes
        if (deafened)
        {
            member.IsMuted = true;
        }
        ctx.Db.VoiceChannelMember.UserId.Update(member);
    }

    /// <summary>
    /// Sets the volume for a specific user (stored server-side for persistence).
    /// </summary>
    [Reducer]
    public static void SetUserVolume(ReducerContext ctx, Identity targetUser, byte volume)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (volume > 200)
        {
            throw new Exception("Volume must be between 0 and 200");
        }

        // Check if setting already exists
        var existing = ctx.Db.UserVolumeSettings.ByUser.Filter(sender)
            .FirstOrDefault(s => s.TargetUserId == targetUser);

        if (existing is not null)
        {
            existing.Volume = volume;
            ctx.Db.UserVolumeSettings.Id.Update(existing);
        }
        else
        {
            ctx.Db.UserVolumeSettings.Insert(new UserVolumeSettings
            {
                Id = 0, // Auto-increment
                UserId = sender,
                TargetUserId = targetUser,
                Volume = volume
            });
        }
    }

    /// <summary>
    /// Saves the user's audio settings (mic/output volume, sensitivity, devices, etc.)
    /// </summary>
    [Reducer]
    public static void SaveAudioSettings(
        ReducerContext ctx,
        string? inputDeviceId,
        string? outputDeviceId,
        byte microphoneVolume,
        byte outputVolume,
        byte micSensitivity,
        bool hearSelf)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (microphoneVolume > 200)
        {
            throw new Exception("Microphone volume must be between 0 and 200");
        }
        if (outputVolume > 200)
        {
            throw new Exception("Output volume must be between 0 and 200");
        }
        if (micSensitivity > 100)
        {
            throw new Exception("Mic sensitivity must be between 0 and 100");
        }

        // Check if settings exist
        if (ctx.Db.UserAudioSettings.UserId.Find(sender) is UserAudioSettings existing)
        {
            existing.InputDeviceId = inputDeviceId;
            existing.OutputDeviceId = outputDeviceId;
            existing.MicrophoneVolume = microphoneVolume;
            existing.OutputVolume = outputVolume;
            existing.MicSensitivity = micSensitivity;
            existing.HearSelf = hearSelf;
            ctx.Db.UserAudioSettings.UserId.Update(existing);
        }
        else
        {
            ctx.Db.UserAudioSettings.Insert(new UserAudioSettings
            {
                UserId = sender,
                InputDeviceId = inputDeviceId,
                OutputDeviceId = outputDeviceId,
                MicrophoneVolume = microphoneVolume,
                OutputVolume = outputVolume,
                MicSensitivity = micSensitivity,
                HearSelf = hearSelf
            });
        }
    }

    /// <summary>
    /// Sends an audio frame to all other members of the user's current voice channel.
    /// Uses event tables for efficient real-time streaming - frames are broadcast
    /// to subscribers and automatically deleted (not persisted).
    /// </summary>
    [Reducer]
    public static void SendAudioFrame(
        ReducerContext ctx,
        uint sequence,
        uint sampleRate,
        byte channels,
        float rms,
        byte[] opusData)
    {
        EnsureAuthorized(ctx);
        var sender = ctx.Sender;

        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is not VoiceChannelMember member)
        {
            // Silently ignore - user not in a channel
            return;
        }

        if (member.IsMuted)
        {
            // Silently ignore - user is muted
            return;
        }

        // Validate frame size (max ~16KB for Opus)
        if (opusData.Length > 16384)
        {
            return; // Drop oversized frames
        }

        // Insert into event table - broadcasts to all subscribers, then auto-deleted
        ctx.Db.AudioFrameEvent.Insert(new AudioFrameEvent
        {
            ChannelId = member.ChannelId,
            FromUser = sender,
            Sequence = sequence,
            SampleRate = sampleRate,
            Channels = channels,
            Rms = rms,
            OpusData = opusData
        });
    }

    // ===========================
    // Lifecycle Reducers
    // ===========================

    [Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        // Create a default "General" voice channel if none exist
        if (!ctx.Db.VoiceChannel.Iter().Any())
        {
            ctx.Db.VoiceChannel.Insert(new VoiceChannel
            {
                Id = 0,
                Name = "General",
                MaxMembers = 25,
                CreatedAt = ctx.Timestamp
            });
            Log.Info("Created default 'General' voice channel");
        }
    }

    [Reducer(ReducerKind.ClientConnected)]
    public static void ClientConnected(ReducerContext ctx)
    {
        EnsureAuthorized(ctx);
        Log.Info($"Connect {ctx.Sender}");

        if (ctx.Db.User.Identity.Find(ctx.Sender) is User user)
        {
            user.Online = true;
            ctx.Db.User.Identity.Update(user);
        }
        else
        {
            ctx.Db.User.Insert(
                new User
                {
                    Name = null,
                    Identity = ctx.Sender,
                    Online = true,
                }
            );
        }
    }

    [Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        var sender = ctx.Sender;

        // Remove from voice channel on disconnect
        if (ctx.Db.VoiceChannelMember.UserId.Find(sender) is VoiceChannelMember _)
        {
            ctx.Db.VoiceChannelMember.UserId.Delete(sender);
            Log.Info($"User {sender} removed from voice channel on disconnect");
        }

        if (ctx.Db.User.Identity.Find(sender) is User user)
        {
            user.Online = false;
            ctx.Db.User.Identity.Update(user);
        }
        else
        {
            Log.Warn("Warning: No user found for disconnected client.");
        }
    }
}
