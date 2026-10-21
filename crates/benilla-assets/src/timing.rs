//! `WOW_ASSET_TIME=1`: one line per completed asset load with its wall time on the loader's
//! thread. Off by default, since a cold zone decodes thousands of BLPs and the line costs a write
//! each; on, it separates decode time from the main thread's placement and upload work when a
//! load is slow.

use std::sync::OnceLock;
use std::time::Instant;

fn enabled() -> bool {
    static ON: OnceLock<bool> = OnceLock::new();
    *ON.get_or_init(|| std::env::var("WOW_ASSET_TIME").as_deref() == Ok("1"))
}

/// Arm a timer, or `None` when the instrument is off.
pub(crate) fn start() -> Option<Instant> {
    enabled().then(Instant::now)
}

/// Log one asset's decode time; `path` is the asset's own URL.
pub(crate) fn log(kind: &str, path: &str, started: Option<Instant>) {
    if let Some(t0) = started {
        bevy::log::info!(
            "asset: {kind} {path} decoded in {:.2} ms",
            t0.elapsed().as_secs_f64() * 1000.0
        );
    }
}
