use crate::logic::{self, ExitStep};
use crate::models::AppSettings;
use serde::Serialize;
use std::sync::Mutex;
use std::time::{Duration, Instant};

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Notice {
    pub id: u64,
    pub text: String,
    pub level: String,
}

struct State {
    message: String,
    lane: String,
    rtt_ms: i32,
    exit_ip: Option<String>,
    pending_exit: Option<String>,
    bad_streak: u32,
    cooldown_until: Option<Instant>,
    notices: Vec<Notice>,
    next_notice: u64,
    pending_noted: bool,
}

pub struct Runtime {
    inner: Mutex<State>,
}

impl Runtime {
    pub fn new(settings: &AppSettings) -> Self {
        Self {
            inner: Mutex::new(State {
                message: settings.guardian_message.clone(),
                lane: if settings.adaptive_lane_state.is_empty() { "idle".into() } else { settings.adaptive_lane_state.clone() },
                rtt_ms: settings.last_measured_rtt_ms,
                exit_ip: settings.last_known_exit_ip.clone(),
                pending_exit: None,
                bad_streak: 0,
                cooldown_until: None,
                notices: Vec::new(),
                next_notice: 1,
                pending_noted: false,
            }),
        }
    }

    pub fn set_sample(&self, rtt: i32, lane: &str) {
        let mut state = self.inner.lock().expect("runtime");
        if rtt > 0 { state.rtt_ms = rtt; }
        if !lane.is_empty() { state.lane = lane.into(); }
    }

    pub fn latency_ready(&self, measured: i32, budget: i32) -> bool {
        let mut state = self.inner.lock().expect("runtime");
        let cooling = state.cooldown_until.is_some_and(|until| until > Instant::now());
        let (streak, switch) = logic::next_latency(state.bad_streak, measured, budget, cooling);
        state.bad_streak = streak;
        switch
    }

    pub fn arm_cooldown(&self, seconds: u64) {
        let mut state = self.inner.lock().expect("runtime");
        state.cooldown_until = Some(Instant::now() + Duration::from_secs(seconds));
        state.bad_streak = 0;
    }

    pub fn observe_exit(&self, ip: &str) -> ExitStep {
        let mut state = self.inner.lock().expect("runtime");
        let (step, known, pending) = logic::next_exit(&state.exit_ip, &state.pending_exit, ip);
        state.exit_ip = known;
        state.pending_exit = pending;
        if step != ExitStep::Pending { state.pending_noted = false; }
        step
    }

    pub fn note_pending_once(&self, text: &str) -> bool {
        let mut state = self.inner.lock().expect("runtime");
        if state.pending_noted { return false; }
        state.pending_noted = true;
        push(&mut state, "info", text);
        true
    }

    pub fn note(&self, level: &str, text: &str) {
        let mut state = self.inner.lock().expect("runtime");
        push(&mut state, level, text);
    }

    pub fn snapshot(&self) -> (String, String, i32, Option<String>) {
        let state = self.inner.lock().expect("runtime");
        (state.message.clone(), state.lane.clone(), state.rtt_ms, state.exit_ip.clone())
    }

    pub fn notices_after(&self, after: u64) -> Vec<Notice> {
        let state = self.inner.lock().expect("runtime");
        state.notices.iter().filter(|n| n.id > after).cloned().collect()
    }
}

fn push(state: &mut State, level: &str, text: &str) {
    let text = text.trim();
    if text.is_empty() { return; }
    state.message = text.to_string();
    let id = state.next_notice;
    state.next_notice = state.next_notice.saturating_add(1);
    state.notices.push(Notice { id, text: text.to_string(), level: level.to_string() });
    let extra = state.notices.len().saturating_sub(20);
    if extra > 0 { state.notices.drain(0..extra); }
}
