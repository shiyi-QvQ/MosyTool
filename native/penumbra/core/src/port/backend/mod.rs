#[cfg(not(any(feature = "nusb", feature = "libusb", feature = "serial")))]
compile_error!(
    "At least one of the features 'nusb', 'libusb', or 'serial' must be enabled for the port backend."
);

#[cfg(feature = "nusb")]
mod usb_backend;
#[cfg(feature = "nusb")]
pub use usb_backend::UsbMTKPort;
#[cfg(feature = "serial")]
mod serial_backend;
#[cfg(feature = "serial")]
pub use serial_backend::SerialMTKPort;
#[cfg(feature = "libusb")]
mod libusb_backend;
#[cfg(feature = "libusb")]
pub use libusb_backend::LibUsbMTKPort;
