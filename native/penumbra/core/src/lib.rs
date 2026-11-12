/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
#![feature(trait_alias)]

pub mod activity;
mod auth;
pub mod da;
pub mod device;
mod devinfo;
pub mod error;
#[cfg(feature = "exploits")]
pub mod exploit;
mod log_buffer;
pub mod macros;
pub mod port;
mod preloader;
pub mod storage;
pub mod traits;
mod utils;
pub use acon::{MMIO, SoC};
pub use auth::{AuthManager, SignData, SignPurpose, SignRequest, Signer};
pub use da::{BootMode, DownloadProtocol};
pub use device::{Device, DeviceBuilder};
pub use devinfo::{DevInfo, DevInfoData};
pub use error::{Error, Result};
pub use hacc;
pub use log_buffer::{DeviceLog, OnPush};
pub use port::{MtkPort, PortBackend, PortType};
pub use preloader::PlProtocol;
pub use storage::{
    Gpt,
    Partition,
    PartitionKind,
    RPMB_FRAME_DATA_SZ,
    RpmbRegion,
    Storage,
    StorageKind,
    StorageType,
};

pub const VERSION: &str = env!("CARGO_PKG_VERSION");
