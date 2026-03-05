use spacetimedb::{reducer, table, Identity, ReducerContext, Table, Timestamp};

// ===========================
// Configuration
// ===========================

const ALLOWED_ISSUERS: &[&str] = &[
    // Example: "https://your-tenant.us.auth0.com/"
];

const ALLOWED_AUDIENCES: &[&str] = &[
    // Example: "your_auth0_client_id"
];

fn ensure_authorized(ctx: &ReducerContext) -> Result<(), String> {
    let auth = ctx.sender_auth();
    let jwt_opt = auth.jwt();
    let jwt = jwt_opt
        .as_ref()
        .ok_or("Unauthorized: JWT is required")?;

    if !ALLOWED_ISSUERS.is_empty()
        && !ALLOWED_ISSUERS.contains(&&*jwt.issuer())
    {
        return Err(format!("Unauthorized: invalid issuer {}", jwt.issuer()));
    }

    if !ALLOWED_AUDIENCES.is_empty()
        && !jwt
            .audience()
            .iter()
            .any(|aud| ALLOWED_AUDIENCES.contains(&&**aud))
    {
        return Err("Unauthorized: invalid audience".to_string());
    }

    Ok(())
}

// ===========================
// User Table
// ===========================

#[table(accessor = user, public)]
pub struct User {
    #[primary_key]
    pub identity: Identity,
    pub name: Option<String>,
    pub online: bool,
}

// ===========================
// Message Table
// ===========================

#[table(accessor = message, public)]
pub struct Message {
    pub sender: Identity,
    pub sent: Timestamp,
    pub text: String,
}

// ===========================
// Voice Channel Tables
// ===========================

#[table(accessor = voice_channel, public)]
pub struct VoiceChannel {
    #[primary_key]
    #[auto_inc]
    pub id: u32,
    pub name: String,
    pub max_members: u32,
    pub created_at: Timestamp,
    pub created_by: Identity,
}

/// Tracks users currently in a voice channel.
#[table(accessor = voice_channel_member, public, index(accessor = by_channel, btree(columns = [channel_id])))]
pub struct VoiceChannelMember {
    #[primary_key]
    pub user_id: Identity,
    pub channel_id: u32,
    pub is_muted: bool,
    pub is_deafened: bool,
    pub joined_at: Timestamp,
}

/// Per-user volume settings for other users in voice channels.
#[table(accessor = user_volume_settings, public, index(accessor = by_user, btree(columns = [user_id])))]
pub struct UserVolumeSettings {
    #[primary_key]
    #[auto_inc]
    pub id: u32,
    pub user_id: Identity,
    pub target_user_id: Identity,
    /// Volume level 0-200 where 100 is default (1.0x)
    pub volume: u8,
}

/// Event table for streaming audio frames. Rows are ephemeral - broadcast to
/// subscribers then automatically deleted.
#[table(accessor = audio_frame_event, public, event)]
pub struct AudioFrameEvent {
    pub channel_id: u32,
    pub from_user: Identity,
    pub sequence: u32,
    /// Sample rate in Hz (e.g., 16000, 24000, 48000)
    pub sample_rate: u32,
    /// Number of audio channels (1=mono, 2=stereo)
    pub channels: u8,
    /// RMS level for voice activity detection on receiver side
    pub rms: f32,
    /// Opus-encoded audio data for efficient transmission
    pub opus_data: Vec<u8>,
}

/// Persistent audio settings for users.
#[table(accessor = user_audio_settings, public)]
pub struct UserAudioSettings {
    #[primary_key]
    pub user_id: Identity,
    /// Input device ID (None for default)
    pub input_device_id: Option<String>,
    /// Output device ID (None for default)
    pub output_device_id: Option<String>,
    /// Microphone volume 0-200 (100 = normal)
    pub microphone_volume: u8,
    /// Output volume 0-200 (100 = normal)
    pub output_volume: u8,
    /// Mic sensitivity threshold 0-100 (scaled to 0.0-0.1)
    pub mic_sensitivity: u8,
    /// Whether to hear your own voice (loopback)
    pub hear_self: bool,
}

// ===========================
// User Reducers
// ===========================

#[reducer]
pub fn set_name(ctx: &ReducerContext, name: String) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let name = validate_name(&name)?;

    if let Some(user) = ctx.db.user().identity().find(ctx.sender()) {
        ctx.db.user().identity().update(User {
            name: Some(name),
            ..user
        });
    }

    Ok(())
}

fn validate_name(name: &str) -> Result<String, String> {
    if name.is_empty() {
        return Err("Names must not be empty".to_string());
    }
    if name.len() > 100 {
        return Err("Name must be 100 characters or less".to_string());
    }
    Ok(name.to_string())
}

// ===========================
// Message Reducers
// ===========================

#[reducer]
pub fn send_message(ctx: &ReducerContext, text: String) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let text = validate_message(&text)?;
    log::info!("{}", text);
    ctx.db.message().insert(Message {
        sender: ctx.sender(),
        text,
        sent: ctx.timestamp,
    });
    Ok(())
}

fn validate_message(text: &str) -> Result<String, String> {
    if text.is_empty() {
        return Err("Messages must not be empty".to_string());
    }
    if text.len() > 4000 {
        return Err("Messages must be 4000 characters or less".to_string());
    }
    Ok(text.to_string())
}

// ===========================
// Voice Channel Reducers
// ===========================

#[reducer]
pub fn create_voice_channel(
    ctx: &ReducerContext,
    name: String,
    max_members: u32,
) -> Result<(), String> {
    ensure_authorized(ctx)?;

    let name = name.trim().to_string();
    if name.is_empty() {
        return Err("Channel name cannot be empty".to_string());
    }
    if name.len() > 50 {
        return Err("Channel name must be 50 characters or less".to_string());
    }

    let max_members = if max_members == 0 { 25 } else { max_members };
    if max_members > 100 {
        return Err("Max members cannot exceed 100".to_string());
    }

    ctx.db.voice_channel().insert(VoiceChannel {
        id: 0, // auto-inc
        name: name.clone(),
        max_members,
        created_at: ctx.timestamp,
        created_by: ctx.sender(),
    });

    log::info!("Voice channel '{}' created by {:?}", name, ctx.sender());
    Ok(())
}

#[reducer]
pub fn delete_voice_channel(ctx: &ReducerContext, channel_id: u32) -> Result<(), String> {
    ensure_authorized(ctx)?;

    let channel = ctx
        .db
        .voice_channel()
        .id()
        .find(&channel_id)
        .ok_or("Voice channel not found")?;

    if channel.created_by != ctx.sender() {
        return Err("Only the channel creator can delete it".to_string());
    }

    // Remove all members from this channel
    let members_to_remove: Vec<_> = ctx
        .db
        .voice_channel_member()
        .by_channel()
        .filter(&channel_id)
        .collect();
    for member in members_to_remove {
        ctx.db
            .voice_channel_member()
            .user_id()
            .delete(&member.user_id);
    }

    ctx.db.voice_channel().id().delete(&channel_id);
    log::info!(
        "Voice channel '{}' (ID: {}) deleted",
        channel.name,
        channel_id
    );
    Ok(())
}

#[reducer]
pub fn join_voice_channel(ctx: &ReducerContext, channel_id: u32) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    let channel = ctx
        .db
        .voice_channel()
        .id()
        .find(&channel_id)
        .ok_or("Voice channel not found")?;

    // Check if already in a channel
    if let Some(existing) = ctx.db.voice_channel_member().user_id().find(sender) {
        if existing.channel_id == channel_id {
            return Ok(()); // Already in this channel
        }
        // Leave current channel first
        ctx.db.voice_channel_member().user_id().delete(&sender);
    }

    // Check member limit
    let current_count = ctx
        .db
        .voice_channel_member()
        .by_channel()
        .filter(&channel_id)
        .count();
    if current_count >= channel.max_members as usize {
        return Err("Voice channel is full".to_string());
    }

    ctx.db.voice_channel_member().insert(VoiceChannelMember {
        user_id: sender,
        channel_id,
        is_muted: false,
        is_deafened: false,
        joined_at: ctx.timestamp,
    });

    log::info!("User {:?} joined voice channel '{}'", sender, channel.name);
    Ok(())
}

#[reducer]
pub fn leave_voice_channel(ctx: &ReducerContext) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    if let Some(member) = ctx.db.voice_channel_member().user_id().find(sender) {
        ctx.db.voice_channel_member().user_id().delete(&sender);
        log::info!(
            "User {:?} left voice channel {}",
            sender,
            member.channel_id
        );
    }

    Ok(())
}

#[reducer]
pub fn set_voice_muted(ctx: &ReducerContext, muted: bool) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    let member = ctx
        .db
        .voice_channel_member()
        .user_id()
        .find(sender)
        .ok_or("Not in a voice channel")?;

    ctx.db
        .voice_channel_member()
        .user_id()
        .update(VoiceChannelMember {
            is_muted: muted,
            ..member
        });

    Ok(())
}

#[reducer]
pub fn set_voice_deafened(ctx: &ReducerContext, deafened: bool) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    let member = ctx
        .db
        .voice_channel_member()
        .user_id()
        .find(sender)
        .ok_or("Not in a voice channel")?;

    ctx.db
        .voice_channel_member()
        .user_id()
        .update(VoiceChannelMember {
            is_deafened: deafened,
            is_muted: if deafened { true } else { member.is_muted },
            ..member
        });

    Ok(())
}

#[reducer]
pub fn set_user_volume(
    ctx: &ReducerContext,
    target_user: Identity,
    volume: u8,
) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    if volume > 200 {
        return Err("Volume must be between 0 and 200".to_string());
    }

    let existing = ctx
        .db
        .user_volume_settings()
        .by_user()
        .filter(&sender)
        .find(|s| s.target_user_id == target_user);

    if let Some(existing) = existing {
        ctx.db
            .user_volume_settings()
            .id()
            .update(UserVolumeSettings {
                volume,
                ..existing
            });
    } else {
        ctx.db.user_volume_settings().insert(UserVolumeSettings {
            id: 0,
            user_id: sender,
            target_user_id: target_user,
            volume,
        });
    }

    Ok(())
}

#[reducer]
pub fn save_audio_settings(
    ctx: &ReducerContext,
    input_device_id: Option<String>,
    output_device_id: Option<String>,
    microphone_volume: u8,
    output_volume: u8,
    mic_sensitivity: u8,
    hear_self: bool,
) -> Result<(), String> {
    ensure_authorized(ctx)?;
    let sender = ctx.sender();

    if microphone_volume > 200 {
        return Err("Microphone volume must be between 0 and 200".to_string());
    }
    if output_volume > 200 {
        return Err("Output volume must be between 0 and 200".to_string());
    }
    if mic_sensitivity > 100 {
        return Err("Mic sensitivity must be between 0 and 100".to_string());
    }

    if let Some(existing) = ctx.db.user_audio_settings().user_id().find(sender) {
        ctx.db
            .user_audio_settings()
            .user_id()
            .update(UserAudioSettings {
                input_device_id,
                output_device_id,
                microphone_volume,
                output_volume,
                mic_sensitivity,
                hear_self,
                ..existing
            });
    } else {
        ctx.db.user_audio_settings().insert(UserAudioSettings {
            user_id: sender,
            input_device_id,
            output_device_id,
            microphone_volume,
            output_volume,
            mic_sensitivity,
            hear_self,
        });
    }

    Ok(())
}

#[reducer]
pub fn send_audio_frame(
    ctx: &ReducerContext,
    sequence: u32,
    sample_rate: u32,
    channels: u8,
    rms: f32,
    opus_data: Vec<u8>,
) {
    if ensure_authorized(ctx).is_err() {
        return;
    }
    let sender = ctx.sender();

    let member = match ctx.db.voice_channel_member().user_id().find(sender) {
        Some(m) => m,
        None => return,
    };

    if member.is_muted {
        return;
    }

    if opus_data.len() > 16384 {
        return;
    }

    if !matches!(sample_rate, 8000 | 12000 | 16000 | 24000 | 48000) {
        return;
    }

    if !matches!(channels, 1 | 2) {
        return;
    }

    ctx.db.audio_frame_event().insert(AudioFrameEvent {
        channel_id: member.channel_id,
        from_user: sender,
        sequence,
        sample_rate,
        channels,
        rms,
        opus_data,
    });
}

// ===========================
// Lifecycle Reducers
// ===========================

#[reducer(init)]
pub fn init(ctx: &ReducerContext) {
    if ctx.db.voice_channel().iter().next().is_none() {
        ctx.db.voice_channel().insert(VoiceChannel {
            id: 0,
            name: "General".to_string(),
            max_members: 25,
            created_at: ctx.timestamp,
            created_by: ctx.identity(),
        });
        log::info!("Created default 'General' voice channel");
    }
}

#[reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) {
    if ensure_authorized(ctx).is_err() {
        return;
    }
    log::info!("Connect {:?}", ctx.sender());

    if let Some(user) = ctx.db.user().identity().find(ctx.sender()) {
        ctx.db.user().identity().update(User {
            online: true,
            ..user
        });
    } else {
        ctx.db.user().insert(User {
            identity: ctx.sender(),
            name: None,
            online: true,
        });
    }
}

#[reducer(client_disconnected)]
pub fn client_disconnected(ctx: &ReducerContext) {
    let sender = ctx.sender();

    if ctx.db.voice_channel_member().user_id().find(sender).is_some() {
        ctx.db.voice_channel_member().user_id().delete(&sender);
        log::info!("User {:?} removed from voice channel on disconnect", sender);
    }

    if let Some(user) = ctx.db.user().identity().find(sender) {
        ctx.db.user().identity().update(User {
            online: false,
            ..user
        });
    } else {
        log::warn!("Warning: No user found for disconnected client.");
    }
}
