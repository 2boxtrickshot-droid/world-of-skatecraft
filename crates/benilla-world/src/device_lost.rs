//! The GPU banner and the wgpu device-lost callback. A removed device is fatal — nothing here
//! recreates it — but without the callback a DX12 removal (`DXGI_ERROR_DEVICE_REMOVED`,
//! `0x887A0005`) reaches the player as a later submit's opaque panic, with the driver's reason
//! thrown away. The callback names the reason and the driver's message, which is the evidence a
//! hardware report needs; the banner records the backend and the limits the driver accepted.

use std::time::{Duration, Instant};

use bevy::prelude::*;
use bevy::render::renderer::RenderAdapterInfo;
use bevy::render::renderer::RenderDevice;
use bevy::render::{Render, RenderApp};

pub(crate) struct DeviceLostPlugin;

impl Plugin for DeviceLostPlugin {
    fn build(&self, app: &mut App) {
        let Some(render_app) = app.get_sub_app_mut(RenderApp) else {
            return;
        };
        render_app.add_systems(Render, (install, report_memory));
    }
}

/// `$WOW_GPU_MEM=<seconds>`: log the wgpu allocator's live bytes this often — the number a
/// memory-pressure device loss (the DX12 `0x887A0005` hypothesis) has to be argued from. Off by
/// default: the report walks every allocation.
fn memory_report_interval() -> Option<Duration> {
    std::env::var("WOW_GPU_MEM")
        .ok()
        .and_then(|v| v.parse::<f32>().ok())
        .filter(|s| *s > 0.0)
        .map(Duration::from_secs_f32)
}

fn log_memory(device: &RenderDevice) {
    if let Some(report) = device.wgpu_device().generate_allocator_report() {
        info!(
            "gpu: memory allocated {} MiB, reserved {} MiB in {} block(s)",
            report.total_allocated_bytes / (1024 * 1024),
            report.total_reserved_bytes / (1024 * 1024),
            report.blocks.len(),
        );
    }
}

/// Once, on the render world's first frame: name the adapter and its limits, and arm the report.
fn install(adapter: Res<RenderAdapterInfo>, device: Res<RenderDevice>, mut done: Local<bool>) {
    if *done {
        return;
    }
    *done = true;
    let info = &adapter.0;
    let limits = device.limits();
    info!(
        "gpu: {} [{:?}, driver {} / {}] max_texture_2d={} max_buffer={}MiB \
         max_storage_textures_per_stage={}",
        info.name,
        info.backend,
        info.driver,
        info.driver_info,
        limits.max_texture_dimension_2d,
        limits.max_buffer_size / (1024 * 1024),
        limits.max_storage_textures_per_shader_stage,
    );
    log_memory(&device);
    device
        .wgpu_device()
        .set_device_lost_callback(|reason, message| {
            error!("gpu: device lost ({reason:?}): {message}");
            // The tracing layer may already be gone when the driver drops us; stderr always lands.
            eprintln!("gpu: device lost ({reason:?}): {message}");
        });
}

/// `WOW_GPU_MEM`: the periodic allocator report.
fn report_memory(device: Res<RenderDevice>, mut last: Local<Option<Instant>>) {
    let Some(every) = memory_report_interval() else {
        return;
    };
    let now = Instant::now();
    if last.is_some_and(|t| now.duration_since(t) < every) {
        return;
    }
    *last = Some(now);
    log_memory(&device);
}
