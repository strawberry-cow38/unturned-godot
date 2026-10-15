//! The source FEATURE_CLIPBOARD_PRIMARY path, separate from Ctrl+C/V.
//! One worker owns the Linux selection for its lifetime; clipboard services can
//! block, so initialization and reads never run on the rendering thread.
use bevy::prelude::*;
use std::sync::mpsc::{self, Receiver, Sender};

enum Request {
    Read(Sender<Option<String>>),
    Write(String),
}
pub(super) struct Read(std::sync::Mutex<Receiver<Option<String>>>);
impl Read {
    pub fn poll(&self) -> Option<Option<String>> {
        match self.0.lock().ok()?.try_recv() {
            Ok(value) => Some(value),
            Err(mpsc::TryRecvError::Empty) => None,
            Err(mpsc::TryRecvError::Disconnected) => Some(None),
        }
    }
}
#[derive(Resource, Default)]
pub(super) struct Primary {
    worker: Option<Sender<Request>>,
}
impl Primary {
    pub fn supported(&self) -> bool {
        cfg!(target_os = "linux")
            && (std::env::var_os("DISPLAY").is_some()
                || std::env::var_os("WAYLAND_DISPLAY").is_some())
    }
    fn worker(&mut self) -> Option<&Sender<Request>> {
        if !self.supported() {
            return None;
        }
        self.worker.get_or_insert_with(|| {
            let (tx, rx) = mpsc::channel();
            std::thread::spawn(move || serve(rx));
            tx
        });
        self.worker.as_ref()
    }
    pub fn read(&mut self) -> Read {
        let (tx, rx) = mpsc::channel();
        if let Some(worker) = self.worker() {
            let _ = worker.send(Request::Read(tx));
        }
        Read(std::sync::Mutex::new(rx))
    }
    pub fn write(&mut self, text: &str) {
        if let Some(worker) = self.worker() {
            let _ = worker.send(Request::Write(text.into()));
        }
    }
}
fn serve(rx: Receiver<Request>) {
    #[cfg(target_os = "linux")]
    {
        use arboard::{GetExtLinux, LinuxClipboardKind, SetExtLinux};
        let mut clipboard = arboard::Clipboard::new().ok();
        while let Ok(request) = rx.recv() {
            match request {
                Request::Read(reply) => {
                    let text = clipboard
                        .as_mut()
                        .and_then(|c| c.get().clipboard(LinuxClipboardKind::Primary).text().ok());
                    let _ = reply.send(text);
                }
                Request::Write(text) => {
                    if let Some(c) = &mut clipboard {
                        let _ = c.set().clipboard(LinuxClipboardKind::Primary).text(text);
                    }
                }
            }
        }
    }
    #[cfg(not(target_os = "linux"))]
    drop(rx);
}
