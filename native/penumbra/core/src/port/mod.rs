/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025 Shomy
*/

mod backend;
use std::fmt::Debug;
use std::time::Duration;

pub use backend::*;
use enum_dispatch::enum_dispatch;

use crate::error::Result;

/// Minimum timeout for reading/writing to the port. This is the default one.
pub const MIN_TIMEOUT: Duration = Duration::from_millis(1000);
/// Maximum timeout for reading/writing to the port. Used only in some cases.
pub const MAX_TIMEOUT: Duration = Duration::from_millis(10000);
/// Maximum time to wait for the port to open to let it settle.
/// On Linux, the port may take a while to be accessible depending on udev rules.
#[allow(dead_code)]
const PORT_OPEN_TIMEOUT: Duration = Duration::from_millis(1500);
/// Poll time to retry opening the port.
#[allow(dead_code)]
const PORT_RETRY_INTERVAL: Duration = Duration::from_millis(50);

/// List of all ports available for connecting and what mode they refer to.
/// Add more entries here for vendor specific ports
#[rustfmt::skip]
pub const KNOWN_PORTS: &[(u16, u16, ConnectionType)] = &[
    (0x0E8D, 0x0003, ConnectionType::Brom),      // Mediatek USB Port (BROM)
    (0x0E8D, 0x6000, ConnectionType::Preloader), // Mediatek USB Port (Preloader)
    (0x0E8D, 0x2000, ConnectionType::Preloader), // Mediatek USB Port (Preloader)
    (0x0E8D, 0x2001, ConnectionType::Da),        // Mediatek USB Port (DA)
    (0x0E8D, 0x20FF, ConnectionType::Preloader), // Mediatek USB Port (Preloader)
    (0x0E8D, 0x3000, ConnectionType::Preloader), // Mediatek USB Port (Preloader)
    (0x1004, 0x6000, ConnectionType::Preloader), // LG USB Port (Preloader)
    (0x22D9, 0x0006, ConnectionType::Preloader), // OPPO USB Port (Preloader)
    (0x0FCE, 0xF200, ConnectionType::Brom),      // Sony USB Port (BROM)
    (0x0FCE, 0xD1E9, ConnectionType::Brom),      // Sony USB Port (BROM XA1)
    (0x0FCE, 0xD1E2, ConnectionType::Brom),      // Sony USB Port (BROM)
    (0x0FCE, 0xD1EC, ConnectionType::Brom),      // Sony USB Port (BROM L1)
    (0x0FCE, 0xD1DD, ConnectionType::Brom),      // Sony USB Port (BROM F3111)
];

#[derive(Debug, PartialEq, Eq, Copy, Clone)]
pub enum ConnectionType {
    Brom,
    Preloader,
    Da,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub enum PortBackend {
    #[default]
    Auto,
    #[cfg(feature = "nusb")]
    Usb,
    #[cfg(feature = "rusb")]
    Libusb,
    #[cfg(feature = "serial")]
    Serial,
}

#[allow(clippy::large_enum_variant)]
#[enum_dispatch(MtkPort)]
pub enum PortType {
    #[cfg(feature = "nusb")]
    Usb(UsbMTKPort),
    #[cfg(feature = "rusb")]
    Libusb(LibUsbMTKPort),
    #[cfg(feature = "serial")]
    Serial(SerialMTKPort),
}

impl PortType {
    pub fn find_device(
        vid: Option<u16>,
        pid: Option<u16>,
        backend: PortBackend,
    ) -> Result<Option<Self>> {
        match backend {
            PortBackend::Auto => {
                #[cfg(feature = "nusb")]
                {
                    if let Some(port) = UsbMTKPort::find_device(vid, pid)? {
                        return Ok(Some(Self::Usb(port)));
                    }
                }

                #[cfg(feature = "rusb")]
                {
                    if let Some(port) = LibUsbMTKPort::find_device(vid, pid)? {
                        return Ok(Some(Self::Libusb(port)));
                    }
                }

                #[cfg(feature = "serial")]
                {
                    if let Some(port) = SerialMTKPort::find_device(vid, pid)? {
                        return Ok(Some(Self::Serial(port)));
                    }
                }

                Ok(None)
            }

            #[cfg(feature = "nusb")]
            PortBackend::Usb => Ok(UsbMTKPort::find_device(vid, pid)?.map(Self::Usb)),
            #[cfg(feature = "rusb")]
            PortBackend::Libusb => Ok(LibUsbMTKPort::find_device(vid, pid)?.map(Self::Libusb)),
            #[cfg(feature = "serial")]
            PortBackend::Serial => Ok(SerialMTKPort::find_device(vid, pid)?.map(Self::Serial)),
        }
    }

    pub fn find_and_open(
        vid: Option<u16>,
        pid: Option<u16>,
        backend: PortBackend,
    ) -> Result<Option<Self>> {
        match backend {
            PortBackend::Auto => {
                #[cfg(feature = "nusb")]
                if let Some(mut port) = UsbMTKPort::find_device(vid, pid)?
                    && port.open().is_ok()
                {
                    return Ok(Some(Self::Usb(port)));
                }

                #[cfg(feature = "rusb")]
                if let Some(mut port) = LibUsbMTKPort::find_device(vid, pid)?
                    && port.open().is_ok()
                {
                    return Ok(Some(Self::Libusb(port)));
                }

                #[cfg(feature = "serial")]
                if let Some(mut port) = SerialMTKPort::find_device(vid, pid)?
                    && port.open().is_ok()
                {
                    return Ok(Some(Self::Serial(port)));
                }
            }

            #[cfg(feature = "nusb")]
            PortBackend::Usb => {
                if let Some(mut port) = UsbMTKPort::find_device(vid, pid)? {
                    port.open()?;
                    return Ok(Some(Self::Usb(port)));
                }
            }

            #[cfg(feature = "rusb")]
            PortBackend::Libusb => {
                if let Some(mut port) = LibUsbMTKPort::find_device(vid, pid)? {
                    port.open()?;
                    return Ok(Some(Self::Libusb(port)));
                }
            }

            #[cfg(feature = "serial")]
            PortBackend::Serial => {
                if let Some(mut port) = SerialMTKPort::find_device(vid, pid)? {
                    port.open()?;
                    return Ok(Some(Self::Serial(port)));
                }
            }
        }

        Ok(None)
    }
}

#[enum_dispatch]
pub trait MtkPort: Send {
    fn open(&mut self) -> Result<()>;
    fn close(&mut self) -> Result<()>;
    fn reenumerate(&mut self, vid: u16, pid: u16) -> Result<()>;
    fn read_exact(&mut self, buf: &mut [u8]) -> Result<usize>;
    fn write_all(&mut self, buf: &[u8]) -> Result<()>;
    fn read_u32(&mut self) -> Result<u32> {
        let mut buf = [0u8; 4];
        self.read_exact(&mut buf)?;
        Ok(u32::from_le_bytes(buf))
    }
    fn write_u32(&mut self, value: u32) -> Result<()> {
        let buf = value.to_le_bytes();
        self.write_all(&buf)
    }
    fn read_u16(&mut self) -> Result<u16> {
        let mut buf = [0u8; 2];
        self.read_exact(&mut buf)?;
        Ok(u16::from_le_bytes(buf))
    }
    fn write_u16(&mut self, value: u16) -> Result<()> {
        let buf = value.to_le_bytes();
        self.write_all(&buf)
    }
    fn read_u8(&mut self) -> Result<u8> {
        let mut buf = [0u8; 1];
        self.read_exact(&mut buf)?;
        Ok(buf[0])
    }
    fn write_u8(&mut self, value: u8) -> Result<()> {
        let buf = [value];
        self.write_all(&buf)
    }
    fn flush(&mut self) -> Result<()>;

    fn get_baudrate(&self) -> u32;
    fn get_port_name(&self) -> String;
    fn set_timeout(&mut self, timeout: Duration) -> Result<()>;
    fn get_timeout(&self) -> Duration;
    fn connection_type(&self) -> ConnectionType;
    fn set_connection_type(&mut self, connection_type: ConnectionType) -> Result<()>;

    // Only for USB ports
    fn ctrl_out(
        &mut self,
        request_type: u8,
        request: u8,
        value: u16,
        index: u16,
        data: &[u8],
    ) -> Result<()>;
    fn ctrl_in(
        &mut self,
        request_type: u8,
        request: u8,
        value: u16,
        index: u16,
        len: usize,
    ) -> Result<Vec<u8>>;
}
