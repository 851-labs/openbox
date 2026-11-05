//! Where the game shows: a swap chain of the game's own size (320x240), which the console's compositor
//! scales up to the TV with nearest-neighbour sampling, by the largest whole number that fits (4x, 1280x960,
//! on a 1080p TV), centred, as the port's desktop app shows it.
//!
//! The page gives a Windows.UI.Composition ContainerVisual. Its sprite's brush shows a DirectComposition
//! surface, which wgpu makes the swap chain for. So the GPU (or, where an app hasn't got one, Direct3D 12's
//! software renderer) only ever draws the game's own pixels; the scaling is the compositor's.
//!
//! Composition objects belong to the UI thread: everything here is called on it.

use std::ffi::c_void;
use std::sync::Mutex;
use windows::UI::Composition::{
    CompositionBitmapInterpolationMode, CompositionStretch, ContainerVisual, SpriteVisual,
};
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::System::WinRT::Composition::ICompositorInterop;
use windows::core::{IUnknown, Interface};
use windows_numerics::Vector2;

/// The game's picture, in its pixels.
pub const GAME_SIZE: (u32, u32) = (320, 240);

/// DCompositionCreateSurfaceHandle's COMPOSITIONOBJECT_READ | COMPOSITIONOBJECT_WRITE.
const COMPOSITIONOBJECT_ALL_ACCESS: u32 = 0x0003;

pub struct Screen {
    sprite: SpriteVisual,
    /// The surface the swap chain is made for, which the sprite's brush shows.
    handle: HANDLE,
    /// The page's pixels per effective pixel (2 on the console).
    scale: f32,
}

// The handle is only a kernel handle, and the sprite is agile.
unsafe impl Send for Screen {}

static SCREEN: Mutex<Option<Screen>> = Mutex::new(None);

/// Puts a sprite for the game in `container` (a ContainerVisual, as an IUnknown) of `size` pixels, and
/// returns the surface handle to make the swap chain for (`wgpu::SurfaceTargetUnsafe::SurfaceHandle`).
pub fn attach(container: *mut c_void, size: (u32, u32), scale: f32) -> Result<*mut c_void, String> {
    detach();
    let container = unsafe { IUnknown::from_raw_borrowed(&container) }
        .ok_or("no visual")?
        .cast::<ContainerVisual>()
        .map_err(|e| format!("the page's visual isn't a ContainerVisual: {e}"))?;
    fn error(what: &'static str) -> impl Fn(windows::core::Error) -> String {
        move |e| format!("{what}: {e}")
    }
    let handle = unsafe {
        windows::Win32::Graphics::DirectComposition::DCompositionCreateSurfaceHandle(
            COMPOSITIONOBJECT_ALL_ACCESS,
            None,
        )
    }
    .map_err(error("can't make a composition surface"))?;
    let compositor = container.Compositor().map_err(error("the compositor"))?;
    let surface = unsafe {
        compositor
            .cast::<ICompositorInterop>()
            .map_err(error("the compositor's interop"))?
            .CreateCompositionSurfaceForHandle(handle)
    }
    .map_err(error("can't show the surface"))?;
    let brush = compositor
        .CreateSurfaceBrushWithSurface(&surface)
        .map_err(error("the surface's brush"))?;
    // Every GM pixel the same size and as sharp as the desktop app's.
    brush
        .SetBitmapInterpolationMode(CompositionBitmapInterpolationMode::NearestNeighbor)
        .map_err(error("the brush's sampling"))?;
    // The game may draw a picture of another size (a saved settings.cfg's scale): it keeps its shape.
    brush
        .SetStretch(CompositionStretch::Uniform)
        .map_err(error("the brush's stretch"))?;
    let sprite = compositor.CreateSpriteVisual().map_err(error("the sprite"))?;
    sprite.SetBrush(&brush).map_err(error("the sprite's brush"))?;
    container
        .Children()
        .and_then(|children| children.InsertAtTop(&sprite))
        .map_err(error("adding the sprite"))?;
    let screen = Screen { sprite, handle, scale };
    screen.layout(size);
    let raw = handle.0;
    *SCREEN.lock().unwrap() = Some(screen);
    Ok(raw)
}

/// The page's new size in pixels.
pub fn resize(size: (u32, u32)) {
    if let Some(screen) = SCREEN.lock().unwrap().as_ref() {
        screen.layout(size);
    }
}

/// Takes the game's sprite away and closes the surface handle (the swap chain keeps the surface while it
/// lives).
pub fn detach() {
    if let Some(screen) = SCREEN.lock().unwrap().take() {
        let _ = screen.sprite.SetBrush(None);
        let _ = unsafe { CloseHandle(screen.handle) };
    }
}

impl Screen {
    /// The sprite at the largest whole multiple of the game's size that fits `size` pixels, centred.
    fn layout(&self, size: (u32, u32)) {
        let (w, h) = GAME_SIZE;
        let whole = (size.0 / w).min(size.1 / h).max(1);
        let (sw, sh) = ((w * whole) as f32, (h * whole) as f32);
        let x = ((size.0 as f32 - sw) / 2.0).max(0.0).floor();
        let y = ((size.1 as f32 - sh) / 2.0).max(0.0).floor();
        let s = self.scale;
        let _ = self.sprite.SetSize(Vector2 { X: sw / s, Y: sh / s });
        let _ = self.sprite.SetOffset(windows_numerics::Vector3 { X: x / s, Y: y / s, Z: 0.0 });
    }
}
