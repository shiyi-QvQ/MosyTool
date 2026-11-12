/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

use std::collections::VecDeque;
use std::fmt;
use std::time::Duration;

use log::debug;
use rusb::{Context, Device, DeviceHandle, Direction, Recipient, RequestType, UsbContext};

use crate::error::{ConnectionError, Error, Result};
use crate::port::{ConnectionType, KNOWN_PORTS, MAX_TIMEOUT, MIN_TIMEOUT, MtkPort};

pub struct LibUsbMTKPort {
    handle: DeviceHandle<Context>,
    conn_type: ConnectionType,
    is_open: bool,
    port_name: String,
    in_endpoint: u8,
    out_endpoint: u8,
    timeout: Duration,
    read_buf: VecDeque<u8>,
    temp_buf: Vec<u8>,
}

impl fmt::Debug for LibUsbMTKPort {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "UsbMTKPort {{ port_name: {}, is_open: {} }}", self.port_name, self.is_open)
    }
}

impl LibUsbMTKPort {
    pub fn new(
        handle: DeviceHandle<Context>,
        conn_type: ConnectionType,
        port_name: String,
        in_endpoint: u8,
        out_endpoint: u8,
    ) -> Self {
        Self {
            handle,
            conn_type,
            is_open: false,
            port_name,
            in_endpoint,
            out_endpoint,
            timeout: MIN_TIMEOUT,
            read_buf: VecDeque::new(),
            temp_buf: vec![0u8; 0x80000],
        }
    }

    fn find_bulk_endpoints(device: &Device<Context>) -> Option<(u8, u8)> {
        let config = device.active_config_descriptor().ok()?;
        let mut in_ep = None;
        let mut out_ep = None;

        for interface in config.interfaces() {
            for interface_desc in interface.descriptors() {
                for endpoint in interface_desc.endpoint_descriptors() {
                    if endpoint.transfer_type() == rusb::TransferType::Bulk {
                        match endpoint.direction() {
                            rusb::Direction::In if in_ep.is_none() => {
                                in_ep = Some(endpoint.address());
                            }
                            rusb::Direction::Out if out_ep.is_none() => {
                                out_ep = Some(endpoint.address());
                            }
                            _ => {}
                        }
                    }
                }
            }
        }

        Some((in_ep?, out_ep?))
    }

    pub fn setup_cdc(&mut self) -> Result<()> {
        const CDC_INTERFACE: u16 = 1;
        const SET_LINE_CODING: u8 = 0x20;
        const SET_CONTROL_LINE_STATE: u8 = 0x22;
        const LINE_CODING: [u8; 7] = [0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x08];
        const CONTROL_LINE_STATE: u16 = 0x03;

        let request_type =
            rusb::request_type(Direction::Out, RequestType::Class, Recipient::Interface);

        self.handle
            .write_control(
                request_type,
                SET_LINE_CODING,
                0,
                CDC_INTERFACE,
                &LINE_CODING,
                Duration::from_millis(100),
            )
            .map_err(|_| ConnectionError::CdcSetupFailed)?;

        self.handle
            .write_control(
                request_type,
                SET_CONTROL_LINE_STATE,
                CONTROL_LINE_STATE,
                CDC_INTERFACE,
                &[],
                Duration::from_millis(100),
            )
            .map_err(|_| ConnectionError::CdcSetupFailed)?;

        Ok(())
    }

    pub fn from_device(device: &Device<Context>) -> Option<Self> {
        let descriptor = device.device_descriptor().ok()?;
        let (vid, pid) = (descriptor.vendor_id(), descriptor.product_id());

        let connection_type = KNOWN_PORTS
            .iter()
            .find(|&&(kvid, kpid, _)| kvid == vid && kpid == pid)
            .map(|&(_, _, ct)| ct)
            .unwrap_or(ConnectionType::Preloader);

        let port_name = format!("USB {:04X}:{:04X} (libusb)", vid, pid);
        let handle = device.open().ok()?;
        let (in_endpoint, out_endpoint) = Self::find_bulk_endpoints(device)?;

        Some(Self::new(handle, connection_type, port_name, in_endpoint, out_endpoint))
    }

    pub fn find_device(vid: Option<u16>, pid: Option<u16>) -> Result<Option<Self>> {
        let context = Context::new()?;
        let devices = context.devices()?;
        let devices: Vec<_> = devices.iter().collect();

        for device in devices {
            let Ok(descriptor) = device.device_descriptor() else {
                continue;
            };

            let dev_vid = descriptor.vendor_id();
            let dev_pid = descriptor.product_id();

            if vid.is_none_or(|v| v == dev_vid)
                && pid.is_none_or(|p| p == dev_pid)
                && (vid.is_some() || pid.is_some())
                && let Some(port) = Self::from_device(&device)
            {
                return Ok(Some(port));
            }

            if KNOWN_PORTS.iter().any(|&(k_vid, k_pid, _)| dev_vid == k_vid && dev_pid == k_pid)
                && let Some(port) = Self::from_device(&device)
            {
                return Ok(Some(port));
            }
        }

        Ok(None)
    }
}

impl MtkPort for LibUsbMTKPort {
    fn open(&mut self) -> Result<()> {
        if self.is_open {
            return Ok(());
        }

        for interface in 0..=1 {
            #[cfg(not(target_os = "windows"))]
            {
                match self.handle.kernel_driver_active(interface) {
                    Ok(true) => {
                        self.handle.detach_kernel_driver(interface)?;
                    }
                    Ok(false) => {}
                    Err(_) => {
                        return Err(Error::Connection(ConnectionError::OpenFailed(
                            "Kernel driver check failed (USB)".to_string(),
                        )));
                    }
                }
            }

            self.handle.claim_interface(interface)?;
        }

        if self.conn_type != ConnectionType::Brom
            && let Err(e) = self.setup_cdc()
        {
            debug!("CDC Setup failed (may be ok): {:?}", e);
        }

        self.is_open = true;

        Ok(())
    }

    fn close(&mut self) -> Result<()> {
        if !self.is_open {
            return Ok(());
        }

        for iface in 0..=1 {
            if let Err(e) = self.handle.release_interface(iface) {
                debug!("Could not release interface {}: {:?}", iface, e);
            }
        }

        self.is_open = false;

        Ok(())
    }

    fn reenumerate(&mut self, vid: u16, pid: u16) -> Result<()> {
        self.close()?;

        let start = std::time::Instant::now();
        let poll_interval = Duration::from_millis(200);

        while start.elapsed() < MAX_TIMEOUT {
            if let Ok(context) = Context::new()
                && let Ok(devices) = context.devices()
            {
                for device in devices.iter() {
                    if let Ok(descriptor) = device.device_descriptor()
                        && descriptor.vendor_id() == vid
                        && descriptor.product_id() == pid
                        && let Some(port) = Self::from_device(&device)
                    {
                        *self = port;
                        return self.open();
                    }
                }
            }
            std::thread::sleep(poll_interval);
        }

        Err(Error::Connection(ConnectionError::Timeout))
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> Result<usize> {
        let endpoint = self.in_endpoint;
        let mut total_read = 0;

        if !self.read_buf.is_empty() {
            let available = self.read_buf.len();
            let to_take = std::cmp::min(buf.len(), available);
            let slices = self.read_buf.make_contiguous();
            buf[..to_take].copy_from_slice(&slices[..to_take]);
            self.read_buf.drain(..to_take);
            total_read += to_take;
        }

        while total_read < buf.len() {
            let n = match self.handle.read_bulk(endpoint, &mut self.temp_buf, self.timeout) {
                Ok(n) => n,
                Err(rusb::Error::Timeout) => {
                    if total_read > 0 {
                        for b in buf[..total_read].iter().rev() {
                            self.read_buf.push_front(*b);
                        }
                    }
                    return Err(Error::Timeout);
                }
                Err(e) => return Err(Error::Io(std::io::Error::other(e))),
            };

            if n == 0 {
                continue;
            }

            let needed = buf.len() - total_read;
            let to_copy = std::cmp::min(needed, n);

            buf[total_read..total_read + to_copy].copy_from_slice(&self.temp_buf[..to_copy]);
            total_read += to_copy;

            if to_copy < n {
                self.read_buf.extend(&self.temp_buf[to_copy..n]);
            }
        }

        Ok(total_read)
    }

    fn write_all(&mut self, buf: &[u8]) -> Result<()> {
        let endpoint = self.out_endpoint;
        let mut written = 0;

        while written < buf.len() {
            match self.handle.write_bulk(endpoint, &buf[written..], self.timeout) {
                Ok(0) => return Err(Error::Timeout),
                Ok(n) => written += n,
                Err(rusb::Error::Timeout) => return Err(Error::Timeout),
                Err(e) => return Err(Error::Io(std::io::Error::other(e))),
            }
        }

        Ok(())
    }

    fn flush(&mut self) -> Result<()> {
        Ok(())
    }

    fn get_baudrate(&self) -> u32 {
        0
    }

    fn get_port_name(&self) -> String {
        self.port_name.clone()
    }

    fn set_timeout(&mut self, timeout: Duration) -> Result<()> {
        self.timeout = timeout;
        Ok(())
    }

    fn get_timeout(&self) -> Duration {
        self.timeout
    }

    fn connection_type(&self) -> ConnectionType {
        self.conn_type
    }

    fn set_connection_type(&mut self, connection_type: ConnectionType) -> Result<()> {
        self.conn_type = connection_type;
        Ok(())
    }

    fn ctrl_out(
        &mut self,
        request_type: u8,
        request: u8,
        value: u16,
        index: u16,
        data: &[u8],
    ) -> Result<()> {
        self.handle
            .write_control(request_type, request, value, index, data, self.timeout)
            .map_err(|_| Error::Connection(ConnectionError::CtrlTransferOutFailed))?;

        Ok(())
    }

    fn ctrl_in(
        &mut self,
        request_type: u8,
        request: u8,
        value: u16,
        index: u16,
        len: usize,
    ) -> Result<Vec<u8>> {
        let mut buf = vec![0u8; len];

        let n = self
            .handle
            .read_control(request_type, request, value, index, &mut buf, self.timeout)
            .map_err(|_| Error::Connection(ConnectionError::CtrlTransferInFailed))?;

        buf.truncate(n);
        Ok(buf)
    }
}
