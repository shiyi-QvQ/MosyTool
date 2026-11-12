/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

macro_rules! status {
    ($self:ident, $port:expr, $expected:expr, $msg:expr) => {{
        match $self.get_status($port) {
            Ok(status) if status == $expected => Ok(()),
            Ok(status) => {
                let xflash_err = crate::error::XFlashError::from_code(status);
                log::error!("{}: 0x{:08X} ({})", $msg, status, xflash_err);
                Err(crate::Error::XFlash(xflash_err))
            }
            Err(e) => Err(e),
        }
    }};

    ($self:ident, $port:expr, $expected:expr) => {{ status!($self, $port, $expected, "Status is not expected") }};
}

macro_rules! status_ok {
    ($self:ident, $port:expr, $msg:expr) => {{ status!($self, $port, 0, $msg) }};
    ($self:ident, $port:expr) => {{ status!($self, $port, 0) }};
}

macro_rules! status_any {
    ($self:ident, $port:expr, $($valid:expr),+ $(,)?) => {{
        match $self.get_status($port) {
            Ok(status) if [$($valid),+].contains(&status) => Ok(()),
            Ok(status) => {
                let xflash_err = crate::error::XFlashError::from_code(status);
                log::error!("Status is not expected: 0x{:08X} ({})", status, xflash_err);
                Err(crate::Error::XFlash(xflash_err))
            }
            Err(e) => Err(e),
        }
    }};
}
