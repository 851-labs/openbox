//! spelunky.dll: Spelunky Classic in OpenBox's window.
//!
//! OpenBox's Spelunky page (`OpenBox/Spelunky/`) hands [`spelunky_start`] a composition visual to show
//! the game in, and the folders with the game's assets and saves. The game then runs on a thread of its
//! own, as the port's desktop app runs it (`app::window`), with the same pieces: [`app::play::Play`] steps
//! the game 30 times a second and plays its sound, [`app::render::Renderer`] draws each frame with wgpu
//! (here on Direct3D 12) and [`app::present::Presenter`] puts it in a swap chain of the game's own size,
//! which the compositor scales up to the TV ([`screen`]). The console's controller is GM's joystick 1
//! ([`pad`]).
//!
//! An app on the console has Direct3D 12 only in software (WARP), not the GPU, which only games get. At the
//! game's 320x240 that keeps up: 60 frames a second, a tick in about 6 ms.
//!
//! [`spelunky_start`] and [`spelunky_resize`] are called on the UI thread, which composition needs.

mod pad;
mod report;
mod screen;

use app::input::{Collector, KeyMap, PAD_JOYSTICK};
use app::notice::Notice;
use app::play::{Play, SoundOutput, session_header, sound_bank};
use app::present::{Presenter, Scaling};
use app::render::Renderer;
use app::save::DirStore;
use app::textures::Textures;
use app::window::Game as _;
use assets::Bundle;
use report::{Event, report};
use std::ffi::c_void;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use trace::replay::GamepadConfig;

/// GM's room speed: the game steps 30 times a second.
const TICK: Duration = Duration::from_nanos(1_000_000_000 / 30);
/// Ticks run at most this many at once to catch up after a stall, then the game runs on from now.
const MAX_CATCH_UP: u32 = 4;

/// What the page and the game's thread share.
struct Shared {
    stop: AtomicBool,
    stats: Mutex<String>,
}

struct Running {
    shared: Arc<Shared>,
    thread: JoinHandle<()>,
}

static RUNNING: Mutex<Option<Running>> = Mutex::new(None);

/// Starts the game in `visual` (a Windows.UI.Composition ContainerVisual, as an IUnknown) filling `width` by
/// `height` pixels, at `scale` pixels per effective pixel, with the assets in `assets` (the release's
/// `assets/` folder) and the saves in `data`, both UTF-16 paths ending in a zero. `callback` hears what
/// happens (`report::Event`), on any thread. Returns 0, or -1 if it couldn't start, which `callback` has
/// been told why.
///
/// # Safety
/// On the UI thread, with a live visual and paths as above; `callback` must stay callable until
/// [`spelunky_stop`] returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn spelunky_start(
    visual: *mut c_void,
    assets: *const u16,
    data: *const u16,
    width: u32,
    height: u32,
    scale: f32,
    callback: Option<report::Callback>,
) -> i32 {
    // Any game still running ends first, so it neither writes into the new log nor calls the new page.
    spelunky_stop();
    report::set_callback(callback);
    let (assets, data) = unsafe { (path(assets), path(data)) };
    report::open_log(&data);
    let visual = Visual(visual);
    let size = (width, height);
    match std::panic::catch_unwind(move || unsafe { start(visual, assets, data, size, scale) }) {
        Ok(Ok(running)) => {
            *RUNNING.lock().unwrap() = Some(running);
            0
        }
        Ok(Err(e)) => {
            screen::detach();
            report(Event::Failed, &e);
            -1
        }
        Err(_) => {
            report(Event::Failed, "the game crashed starting (the log has where)");
            -1
        }
    }
}

/// The visual's new size in pixels. On the UI thread.
#[unsafe(no_mangle)]
pub extern "C" fn spelunky_resize(width: u32, height: u32) {
    screen::resize((width, height));
}

/// Ends the game, if it's running, and waits for its thread. Its saves are already written: the game's
/// files are synced after every tick. The callback hears nothing but log lines from then on, so the page
/// can wait on the UI thread.
#[unsafe(no_mangle)]
pub extern "C" fn spelunky_stop() {
    let running = RUNNING.lock().unwrap().take();
    if let Some(r) = running {
        r.shared.stop.store(true, Ordering::Relaxed);
        let _ = r.thread.join();
    }
    screen::detach();
}

/// Writes the last second's figures (frames shown, ticks run, busy time) into `out`, `len` bytes, as UTF-8
/// ending in a zero. Returns the length without the zero.
///
/// # Safety
/// `out` must be writable for `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn spelunky_stats(out: *mut u8, len: u32) -> u32 {
    let text = match RUNNING.lock().unwrap().as_ref() {
        Some(r) => r.shared.stats.lock().unwrap().clone(),
        None => String::new(),
    };
    if out.is_null() || len == 0 {
        return 0;
    }
    let n = text.len().min(len as usize - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(text.as_ptr(), out, n);
        *out.add(n) = 0;
    }
    n as u32
}

unsafe fn path(p: *const u16) -> PathBuf {
    if p.is_null() {
        return PathBuf::new();
    }
    let mut n = 0;
    while unsafe { *p.add(n) } != 0 {
        n += 1;
    }
    PathBuf::from(String::from_utf16_lossy(unsafe {
        std::slice::from_raw_parts(p, n)
    }))
}

/// The page's visual, to pass into `catch_unwind`.
struct Visual(*mut c_void);

impl std::panic::UnwindSafe for Visual {}

/// The UI thread's part: the device, and the swap chain the visual shows. The rest is the game's thread's.
unsafe fn start(
    visual: Visual,
    assets: PathBuf,
    data: PathBuf,
    size: (u32, u32),
    scale: f32,
) -> Result<Running, String> {
    let started = Instant::now();
    let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends: wgpu::Backends::DX12,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    });
    let handle = screen::attach(visual.0, size, scale)?;
    let surface = unsafe {
        instance.create_surface_unsafe(wgpu::SurfaceTargetUnsafe::SurfaceHandle(handle))
    }
    .map_err(|e| format!("can't draw to the page: {e}"))?;
    let gpu = pollster::block_on(app::gpu::request(instance, Some(&surface)))?;
    let info = gpu.adapter.get_info();
    report::log(&format!(
        "graphics: {} ({:?}, driver {} {})",
        info.name, info.backend, info.driver, info.driver_info
    ));
    let presenter = Presenter::new(
        surface,
        &gpu.adapter,
        &gpu.device,
        screen::GAME_SIZE,
        Scaling::Whole,
    )?;
    report::log(&format!(
        "ready in {} ms, in {}x{} pixels at {scale}x",
        started.elapsed().as_millis(),
        size.0,
        size.1,
    ));
    let shared = Arc::new(Shared {
        stop: AtomicBool::new(false),
        stats: Mutex::new(String::new()),
    });
    let thread_shared = shared.clone();
    let thread = std::thread::Builder::new()
        .name("spelunky".into())
        .stack_size(16 << 20)
        .spawn(move || {
            let run = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                run(&thread_shared, gpu.device, gpu.queue, presenter, &assets, &data)
            }));
            // Stopped by the page, which is waiting for this thread: nothing for it but the log.
            if thread_shared.stop.load(Ordering::Relaxed) {
                match run {
                    Ok(Ok(())) => report::log("stopped"),
                    Ok(Err(e)) => report::log(&e),
                    Err(_) => report::log("the game crashed stopping (the log has where)"),
                }
                return;
            }
            match run {
                Ok(Ok(())) => report(Event::Ended, "the game ended"),
                Ok(Err(e)) => report(Event::Failed, &e),
                Err(_) => report(Event::Failed, "the game crashed (the log has where)"),
            }
        })
        .map_err(|e| format!("can't start the game's thread: {e}"))?;
    Ok(Running { shared, thread })
}

/// The controller layout for the console, as the settings program would write it to `gamepad.cfg`
/// (GM's joystick buttons: 1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 8 Menu; -1 and -2 are the left and right
/// triggers, which the game reads off the z axis). Spelunky HD's: A jumps, X whips, B bombs, Y ropes, the
/// right trigger runs. The shipped file is for another pad, and has View (OpenBox's Home) bomb. A
/// `gamepad.cfg` the player saved, from the game's own joystick setup, is laid over this.
fn xbox_gamepad() -> GamepadConfig {
    GamepadConfig {
        jump: 1,
        attack: 3,
        item: 5,
        run: -2,
        bomb: 2,
        rope: 4,
        flare: -1,
        pay: 6,
        start: Some(8),
    }
}

fn load(dir: &Path) -> Result<(Bundle, Textures), String> {
    let read = reader(dir);
    let bundle = Bundle::from_bytes(&read(assets::BUNDLE_FILE)?)
        .map_err(|e| format!("{}: {e}", dir.join(assets::BUNDLE_FILE).display()))?;
    let textures = Textures::load(&bundle, read)?;
    Ok((bundle, textures))
}

fn reader(dir: &Path) -> impl Fn(&str) -> Result<Vec<u8>, String> {
    move |rel: &str| {
        let path = dir.join(rel);
        std::fs::read(&path).map_err(|e| format!("{}: {e}", path.display()))
    }
}

/// Whether there's a sound device to play the game's sound on. cpal panics where it can't reach Windows'
/// device list, rather than failing, which would take the game with it: then the game plays silently.
fn sound_device() -> bool {
    use cpal::traits::HostTrait;
    use cpal::traits::DeviceTrait;
    let device = std::panic::catch_unwind(|| {
        let device = cpal::default_host().default_output_device()?;
        let config = device.default_output_config().ok();
        let name = device.description().ok();
        Some(format!("{name:?}, {config:?}"))
    });
    match device {
        Ok(Some(device)) => {
            report::log(&format!("sound: {device}"));
            true
        }
        Ok(None) => {
            report::log("no sound device: the game plays silently");
            false
        }
        Err(_) => {
            report::log("the sound device couldn't be reached: the game plays silently");
            false
        }
    }
}

/// The game's thread: loads the game, then runs it until it ends or the page stops it.
fn run(
    shared: &Shared,
    device: wgpu::Device,
    queue: wgpu::Queue,
    mut presenter: Presenter,
    assets: &Path,
    data: &Path,
) -> Result<(), String> {
    let started = Instant::now();
    let (bundle, textures) = load(assets)?;
    let notice = Notice::new(&bundle);
    let sounds = sound_bank(&bundle.sounds, reader(assets))?;
    let mut play = Play::new(
        Arc::new(gm::Assets::from_bundle(bundle)),
        sounds,
        if sound_device() {
            SoundOutput::Now
        } else {
            SoundOutput::Off
        },
    )
    .with_store(Box::new(DirStore::new(data.join("save"))));
    if shared.stop.load(Ordering::Relaxed) {
        return Ok(());
    }
    let mut renderer = Renderer::new(device, queue, textures);
    report::log(&format!(
        "loaded in {} ms",
        started.elapsed().as_millis()
    ));

    let mut header = session_header();
    header.joystick = Some(PAD_JOYSTICK);
    header.gamepad = xbox_gamepad();
    // The picture at the game's own size: the compositor scales it up (`screen`).
    header.settings.screen_scale = 1;
    let mut input = Collector::new(KeyMap::Layout);
    input.set_joystick(true);
    let mut pad = pad::Pad::new();
    let mut frame = draw::Frame::default();
    play.begin(&header, &mut frame)?;
    if !frame.commands.is_empty() {
        renderer.render(&frame);
    }

    let mut presenter_size = screen::GAME_SIZE;
    let mut ticks: u64 = 0;
    let mut next = Instant::now();
    let mut stats = Stats::new();
    while !shared.stop.load(Ordering::Relaxed) {
        // The swap chain follows the game's picture, which is its own size unless a saved settings.cfg
        // has another scale.
        if let Some((screen, _)) = renderer.screen() {
            let size = (screen.width(), screen.height());
            if size != presenter_size {
                presenter.resize(renderer.device(), size);
                presenter_size = size;
            }
        }
        let now = Instant::now();
        let mut ran = 0;
        while next <= now {
            if ran == MAX_CATCH_UP {
                next = now + TICK;
                break;
            }
            let busy = Instant::now();
            pad.read(&mut input);
            let events = input.take_tick();
            play.tick(&events, &mut frame)?;
            renderer.render(&frame);
            if let (Some(notice), Some(size)) = (&notice, renderer.shown_size())
                && let Some(over) = notice.commands(ticks, size)
            {
                renderer.draw_over_shown(&over);
            }
            ticks += 1;
            ran += 1;
            next += TICK;
            stats.tick(busy.elapsed());
            if play.ended() {
                presenter.present(&renderer);
                return Ok(());
            }
        }
        // Waits for the display (vsync), so the loop goes round once a refresh: a tick every other one at 60 Hz.
        if presenter.present(&renderer) {
            stats.frames += 1;
        } else {
            std::thread::sleep(Duration::from_millis(4));
        }
        if let Some(line) = stats.second() {
            *shared.stats.lock().unwrap() = line;
        }
    }
    Ok(())
}

/// Frames shown and ticks run each second, and how long the ticks took.
struct Stats {
    since: Instant,
    frames: u32,
    ticks: u32,
    busy: Duration,
    worst: Duration,
    logged: u32,
}

impl Stats {
    fn new() -> Stats {
        Stats {
            since: Instant::now(),
            frames: 0,
            ticks: 0,
            busy: Duration::ZERO,
            worst: Duration::ZERO,
            logged: 0,
        }
    }

    fn tick(&mut self, busy: Duration) {
        self.ticks += 1;
        self.busy += busy;
        self.worst = self.worst.max(busy);
    }

    /// Once a second, the line for the last one (also logged every 10 s).
    fn second(&mut self) -> Option<String> {
        let elapsed = self.since.elapsed();
        if elapsed < Duration::from_secs(1) {
            return None;
        }
        let secs = elapsed.as_secs_f64();
        let line = format!(
            "{:.0} fps, {:.1} ticks/s, tick {:.1} ms avg {:.1} ms worst",
            f64::from(self.frames) / secs,
            f64::from(self.ticks) / secs,
            self.busy.as_secs_f64() * 1000.0 / f64::from(self.ticks.max(1)),
            self.worst.as_secs_f64() * 1000.0,
        );
        if self.logged % 10 == 0 {
            report::log(&line);
        }
        let logged = self.logged + 1;
        *self = Stats::new();
        self.logged = logged;
        Some(line)
    }
}
