use spacetimedb::{Identity, ReducerContext, SpacetimeType, Table, Timestamp, Uuid};

#[spacetimedb::table(accessor = user, public)]
#[derive(Clone)]
pub struct User {
    #[primary_key]
    pub identity: Identity,
    pub nickname: String,
    pub connected_at: Timestamp,
}

#[spacetimedb::table(accessor = chat_message, public)]
#[derive(Clone)]
pub struct ChatMessage {
    #[primary_key]
    #[auto_inc]
    pub id: u64,
    pub sender: Identity,
    pub sent_at: Timestamp,
    pub text: String,
}

#[derive(SpacetimeType, Debug, Copy, Clone, PartialEq, Eq)]
pub enum CallType {
    Voice,
    Video,
}

#[derive(SpacetimeType, Debug, Copy, Clone, PartialEq, Eq)]
pub enum CallState {
    Ringing,
    Active,
}

#[spacetimedb::table(accessor = call_session, public)]
#[derive(Clone)]
pub struct CallSession {
    #[primary_key]
    pub session_id: Uuid,
    pub call_type: CallType,
    pub state: CallState,
    pub caller: Identity,
    pub callee: Identity,
    pub created_at: Timestamp,
    pub answered_at: Option<Timestamp>,
}

/*
  Singleton media settings table (id = 1).

  Change these values via:
    spacetime sql <db> "UPDATE media_settings SET ... WHERE id = 1" -s local
*/
#[spacetimedb::table(accessor = media_settings, public)]
#[derive(Clone)]
pub struct MediaSettings {
    #[primary_key]
    pub id: u32,

    // Audio encode settings
    pub audio_target_sample_rate: u32,    // e.g. 8000, 16000, 24000
    pub audio_frame_ms: u16,              // e.g. 20, 40, 50
    pub audio_max_frame_bytes: u32,       // drop frames larger than this
    pub audio_talking_rms_threshold: f32, // UI threshold on receiver

    // Video encode settings (JPEG frames)
    pub video_width: u16,
    pub video_height: u16,
    pub video_fps: u8,            // send rate (interval = 1000/fps)
    pub video_jpeg_quality: f32,  // 0.0 - 1.0
    pub video_max_frame_bytes: u32,
}

#[spacetimedb::table(accessor = audio_frame_event, public, event)]
#[derive(Clone)]
pub struct AudioFrameEvent {
    pub session_id: Uuid,
    pub from: Identity,
    pub to: Identity,
    pub seq: u32,
    pub sample_rate: u32,
    pub channels: u8,
    pub rms: f32,
    pub pcm16le: Vec<u8>,
}

#[spacetimedb::table(accessor = video_frame_event, public, event)]
#[derive(Clone)]
pub struct VideoFrameEvent {
    pub session_id: Uuid,
    pub from: Identity,
    pub to: Identity,
    pub seq: u32,
    pub width: u16,
    pub height: u16,
    pub jpeg: Vec<u8>,
}

#[spacetimedb::reducer(init)]
pub fn init(ctx: &ReducerContext) {
    // Insert default singleton settings row if missing
    let exists = ctx.db.media_settings().id().find(&1).is_some();
    if !exists {
        ctx.db.media_settings().insert(MediaSettings {
            id: 1,

            audio_target_sample_rate: 16000,
            audio_frame_ms: 50,
            audio_max_frame_bytes: 64000,
            audio_talking_rms_threshold: 0.02,

            video_width: 320,
            video_height: 180,
            video_fps: 5,
            video_jpeg_quality: 0.55,
            video_max_frame_bytes: 512000,
        });
    }
}

#[spacetimedb::reducer(client_connected)]
pub fn client_connected(ctx: &ReducerContext) {
    let who = ctx.sender();
    let now = ctx.timestamp;

    let default_nick = format!("user-{}", who.to_abbreviated_hex());

    if let Some(existing) = ctx.db.user().identity().find(&who) {
        let mut row = existing.clone();
        row.connected_at = now;
        if row.nickname.is_empty() {
            row.nickname = default_nick;
        }
        ctx.db.user().identity().update(row);
    } else {
        ctx.db.user().insert(User {
            identity: who,
            nickname: default_nick,
            connected_at: now,
        });
    }
}

#[spacetimedb::reducer(client_disconnected)]
pub fn client_disconnected(ctx: &ReducerContext) {
    let who = ctx.sender();
    ctx.db.user().identity().delete(&who);

    let mut to_delete: Vec<Uuid> = Vec::new();
    for s in ctx.db.call_session().iter() {
        if s.caller == who || s.callee == who {
            to_delete.push(s.session_id);
        }
    }
    for id in to_delete {
        ctx.db.call_session().session_id().delete(&id);
    }
}

#[spacetimedb::reducer]
pub fn set_nickname(ctx: &ReducerContext, nickname: String) -> Result<(), String> {
    let who = ctx.sender();
    let nickname = nickname.trim().to_string();

    if nickname.is_empty() {
        return Err("Nickname cannot be empty".to_string());
    }
    if nickname.len() > 32 {
        return Err("Nickname must be <= 32 characters".to_string());
    }

    let user = ctx
        .db
        .user()
        .identity()
        .find(&who)
        .ok_or_else(|| "User not found".to_string())?;

    let mut updated = user.clone();
    updated.nickname = nickname;
    ctx.db.user().identity().update(updated);
    Ok(())
}

#[spacetimedb::reducer]
pub fn send_message(ctx: &ReducerContext, text: String) -> Result<(), String> {
    let who = ctx.sender();
    let now = ctx.timestamp;

    let t = text.trim().to_string();
    if t.is_empty() {
        return Err("Message cannot be empty".to_string());
    }
    if t.len() > 500 {
        return Err("Message must be <= 500 characters".to_string());
    }

    ctx.db.chat_message().insert(ChatMessage {
        id: 0,
        sender: who,
        sent_at: now,
        text: t,
    });
    Ok(())
}

#[spacetimedb::reducer]
pub fn request_call(ctx: &ReducerContext, target: Identity, call_type: CallType) -> Result<(), String> {
    let caller = ctx.sender();
    let now = ctx.timestamp;

    if caller == target {
        return Err("Cannot call yourself".to_string());
    }
    if ctx.db.user().identity().find(&target).is_none() {
        return Err("Target is not online".to_string());
    }

    for s in ctx.db.call_session().iter() {
        if (s.caller == caller || s.callee == caller || s.caller == target || s.callee == target)
            && (s.state == CallState::Ringing || s.state == CallState::Active)
        {
            return Err("Caller or callee is already in a call".to_string());
        }
    }

    let session_id = ctx
        .new_uuid_v7()
        .or_else(|_| ctx.new_uuid_v4())
        .map_err(|_| "Failed to generate session id".to_string())?;

    ctx.db.call_session().insert(CallSession {
        session_id,
        call_type,
        state: CallState::Ringing,
        caller,
        callee: target,
        created_at: now,
        answered_at: None,
    });

    Ok(())
}

#[spacetimedb::reducer]
pub fn accept_call(ctx: &ReducerContext, session_id: Uuid) -> Result<(), String> {
    let who = ctx.sender();
    let now = ctx.timestamp;

    let sess = ctx
        .db
        .call_session()
        .session_id()
        .find(&session_id)
        .ok_or_else(|| "Call session not found".to_string())?;

    if sess.callee != who {
        return Err("Only the callee can accept".to_string());
    }
    if sess.state != CallState::Ringing {
        return Err("Call is not ringing".to_string());
    }

    let mut updated = sess.clone();
    updated.state = CallState::Active;
    updated.answered_at = Some(now);
    ctx.db.call_session().session_id().update(updated);
    Ok(())
}

#[spacetimedb::reducer]
pub fn decline_call(ctx: &ReducerContext, session_id: Uuid) -> Result<(), String> {
    let who = ctx.sender();

    let sess = ctx
        .db
        .call_session()
        .session_id()
        .find(&session_id)
        .ok_or_else(|| "Call session not found".to_string())?;

    if sess.callee != who {
        return Err("Only the callee can decline".to_string());
    }

    ctx.db.call_session().session_id().delete(&session_id);
    Ok(())
}

#[spacetimedb::reducer]
pub fn end_call(ctx: &ReducerContext, session_id: Uuid) -> Result<(), String> {
    let who = ctx.sender();

    let sess = ctx
        .db
        .call_session()
        .session_id()
        .find(&session_id)
        .ok_or_else(|| "Call session not found".to_string())?;

    if sess.caller != who && sess.callee != who {
        return Err("Only a participant can end this call".to_string());
    }

    ctx.db.call_session().session_id().delete(&session_id);
    Ok(())
}

fn other_party(sess: &CallSession, who: Identity) -> Option<Identity> {
    if sess.caller == who {
        Some(sess.callee)
    } else if sess.callee == who {
        Some(sess.caller)
    } else {
        None
    }
}

#[spacetimedb::reducer]
pub fn send_audio_frame(
    ctx: &ReducerContext,
    session_id: Uuid,
    to: Identity,
    seq: u32,
    sample_rate: u32,
    channels: u8,
    rms: f32,
    pcm16le: Vec<u8>,
) -> Result<(), String> {
    let who = ctx.sender();

    let sess = ctx
        .db
        .call_session()
        .session_id()
        .find(&session_id)
        .ok_or_else(|| "Call session not found".to_string())?;

    if sess.state != CallState::Active {
        return Err("Call is not active".to_string());
    }

    let peer = other_party(&sess, who).ok_or_else(|| "Not a participant".to_string())?;
    if peer != to {
        return Err("Invalid recipient".to_string());
    }

    if pcm16le.len() > 64_000 {
        return Err("Audio frame too large".to_string());
    }

    ctx.db.audio_frame_event().insert(AudioFrameEvent {
        session_id,
        from: who,
        to,
        seq,
        sample_rate,
        channels,
        rms,
        pcm16le,
    });

    Ok(())
}

#[spacetimedb::reducer]
pub fn send_video_frame(
    ctx: &ReducerContext,
    session_id: Uuid,
    to: Identity,
    seq: u32,
    width: u16,
    height: u16,
    jpeg: Vec<u8>,
) -> Result<(), String> {
    let who = ctx.sender();

    let sess = ctx
        .db
        .call_session()
        .session_id()
        .find(&session_id)
        .ok_or_else(|| "Call session not found".to_string())?;

    if sess.state != CallState::Active {
        return Err("Call is not active".to_string());
    }
    if sess.call_type != CallType::Video {
        return Err("Not a video call".to_string());
    }

    let peer = other_party(&sess, who).ok_or_else(|| "Not a participant".to_string())?;
    if peer != to {
        return Err("Invalid recipient".to_string());
    }

    if jpeg.len() > 512_000 {
        return Err("Video frame too large".to_string());
    }

    ctx.db.video_frame_event().insert(VideoFrameEvent {
        session_id,
        from: who,
        to,
        seq,
        width,
        height,
        jpeg,
    });

    Ok(())
}