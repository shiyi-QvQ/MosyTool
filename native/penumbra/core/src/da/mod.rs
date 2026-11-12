/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
mod protocol;
mod scatter;
pub(crate) mod storage;
mod types;
pub mod xflash;
pub mod xml;
#[cfg(feature = "exploits")]
pub use protocol::DownloadProtocolExt;
pub(crate) use protocol::NOOP_PROGRESS;
pub use protocol::{DaProtocol, DaProtocolParams, DownloadProtocol};
pub use scatter::{ScatterFile, ScatterOp, ScatterPartition};
pub use types::*;
pub use xflash::XFlash;
pub use xml::Xml;
