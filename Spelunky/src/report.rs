//! What the game tells OpenBox: its log (`spelunky.log` in the data folder, and the page's callback, which
//! writes it into OpenBox's own), and that it ended or failed.
//!
//! The port writes its warnings to stderr, which a UWP app hasn't got, so stderr goes to the log file too
//! (`stderr.log`). Panics and wgpu's own warnings and errors are logged.

use std::fs::File;
use std::io::Write;
use std::path::Path;
use std::sync::Mutex;

/// `kind` is an [`Event`]; `message` is UTF-8 ending in a zero, only valid during the call.
pub type Callback = unsafe extern "C" fn(kind: i32, message: *const u8);

#[derive(Clone, Copy)]
pub enum Event {
    Log = 0,
    /// The game couldn't start, or stopped on an error; its thread has finished.
    Failed = 1,
    /// The game ended (Quit on the title screen); its thread has finished.
    Ended = 2,
}

static CALLBACK: Mutex<Option<Callback>> = Mutex::new(None);
static FILE: Mutex<Option<File>> = Mutex::new(None);

pub fn set_callback(callback: Option<Callback>) {
    *CALLBACK.lock().unwrap() = callback;
}

/// Starts the log afresh in `data` (each start), and sends stderr, panics and wgpu's log there too.
pub fn open_log(data: &Path) {
    let _ = std::fs::create_dir_all(data);
    *FILE.lock().unwrap() = File::create(data.join("spelunky.log")).ok();
    // The rest is the process's, once: the data folder is always the same.
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| redirect(data));
}

fn redirect(data: &Path) {
    #[cfg(windows)]
    if let Ok(stderr) = File::create(data.join("stderr.log")) {
        use std::os::windows::io::IntoRawHandle;
        use windows::Win32::Foundation::HANDLE;
        use windows::Win32::System::Console::{STD_ERROR_HANDLE, SetStdHandle};
        // Leaked on purpose: stderr is the process's, for good.
        let handle = HANDLE(stderr.into_raw_handle());
        let _ = unsafe { SetStdHandle(STD_ERROR_HANDLE, handle) };
    }
    std::panic::set_hook(Box::new(|info| {
        let backtrace = std::backtrace::Backtrace::force_capture();
        log(&format!("panic: {info}\n{backtrace}"));
    }));
    static LOGGER: Logger = Logger;
    if log::set_logger(&LOGGER).is_ok() {
        log::set_max_level(log::LevelFilter::Warn);
    }
}

pub fn log(message: &str) {
    report(Event::Log, message);
}

pub fn report(kind: Event, message: &str) {
    if let Some(file) = FILE.lock().unwrap().as_mut() {
        let _ = writeln!(file, "{message}");
        let _ = file.flush();
    }
    let callback = *CALLBACK.lock().unwrap();
    if let Some(callback) = callback {
        let mut text = message.replace('\0', " ").into_bytes();
        text.push(0);
        unsafe { callback(kind as i32, text.as_ptr()) };
    }
}

struct Logger;

impl log::Log for Logger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Warn
    }

    fn log(&self, record: &log::Record) {
        if self.enabled(record.metadata()) {
            log(&format!("{} {}: {}", record.level(), record.target(), record.args()));
        }
    }

    fn flush(&self) {}
}
