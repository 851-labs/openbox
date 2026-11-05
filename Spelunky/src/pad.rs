//! The console's controller as GM's joystick 1, as the port's desktop app reads an XInput pad
//! (`app::input`'s gamepads): buttons 1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 8 Menu, 9 and 10 the sticks; x and y
//! the left stick (y down), z the left trigger minus the right, and the d-pad the POV hat.
//!
//! View (button 7 on a desktop) isn't passed on: it's OpenBox's, and takes the player home.
//!
//! The controllers are read with Windows.Gaming.Input once a tick, so a press and release between two ticks
//! is missed; Windows.Gaming.Input has only the current state. A tap is far longer than a tick. They're
//! read as one: GM has one joystick, and a second pad (or the one OpenBox's `pad` dev command injects)
//! plays along with the first.

use app::input::{Collector, pov, quantize_axis};
use trace::replay::Axis;

#[cfg(windows)]
use windows::Gaming::Input::{Gamepad, GamepadButtons};

/// GM's button number for each of the pad's buttons that the game gets.
#[cfg(windows)]
const BUTTONS: [(GamepadButtons, u8); 9] = [
    (GamepadButtons::A, 1),
    (GamepadButtons::B, 2),
    (GamepadButtons::X, 3),
    (GamepadButtons::Y, 4),
    (GamepadButtons::LeftShoulder, 5),
    (GamepadButtons::RightShoulder, 6),
    (GamepadButtons::Menu, 8),
    (GamepadButtons::LeftThumbstick, 9),
    (GamepadButtons::RightThumbstick, 10),
];

/// Every connected controller's reading, as one: the buttons any holds, each axis as far as any has it.
#[cfg(windows)]
fn reading() -> Option<windows::Gaming::Input::GamepadReading> {
    let pads = Gamepad::Gamepads().ok()?;
    let mut all: Option<windows::Gaming::Input::GamepadReading> = None;
    for pad in pads {
        let Ok(r) = pad.GetCurrentReading() else {
            continue;
        };
        let Some(a) = &mut all else {
            all = Some(r);
            continue;
        };
        let further = |a: f64, b: f64| if b.abs() > a.abs() { b } else { a };
        a.Buttons |= r.Buttons;
        a.LeftThumbstickX = further(a.LeftThumbstickX, r.LeftThumbstickX);
        a.LeftThumbstickY = further(a.LeftThumbstickY, r.LeftThumbstickY);
        a.LeftTrigger = a.LeftTrigger.max(r.LeftTrigger);
        a.RightTrigger = a.RightTrigger.max(r.RightTrigger);
    }
    all
}

pub struct Pad {
    /// Whether a controller was there at the last read, so its going is noticed once.
    connected: bool,
}

impl Pad {
    pub fn new() -> Pad {
        // Windows.Gaming.Input needs the thread in an apartment.
        #[cfg(windows)]
        unsafe {
            use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx};
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        }
        Pad { connected: false }
    }

    /// Hands the controller's state now to `c`.
    pub fn read(&mut self, c: &mut Collector) {
        #[cfg(windows)]
        {
            let Some(r) = reading() else {
                if std::mem::replace(&mut self.connected, false) {
                    crate::report::log("controller gone");
                    c.pad_lost();
                }
                return;
            };
            if !std::mem::replace(&mut self.connected, true) {
                crate::report::log("controller connected");
            }

            for (button, n) in BUTTONS {
                c.joy_button(n, r.Buttons.contains(button));
            }
            let held = |b: GamepadButtons| r.Buttons.contains(b);
            c.joy_pov(pov(
                held(GamepadButtons::DPadUp),
                held(GamepadButtons::DPadDown),
                held(GamepadButtons::DPadLeft),
                held(GamepadButtons::DPadRight),
            ));
            c.joy_axis(Axis::X, quantize_axis(r.LeftThumbstickX as f32));
            c.joy_axis(Axis::Y, quantize_axis(-r.LeftThumbstickY as f32));
            c.joy_axis(
                Axis::Z,
                quantize_axis((r.LeftTrigger - r.RightTrigger) as f32),
            );
        }
        #[cfg(not(windows))]
        let _ = (c, &mut self.connected);
    }
}
