/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

use wincode::{Deserialize, SchemaRead, SchemaWrite};

use crate::traits::{FromBytes, ToBytes};

#[derive(Default, Debug, SchemaRead, FromBytes)]
#[repr(u32)]
pub enum PartTableCat {
    #[wincode(tag = 0x64)]
    #[default]
    Gpt = 0x64,
    #[wincode(tag = 0x65)]
    Pmt = 0x65,
}

#[derive(Default, SchemaWrite, ToBytes)]
#[repr(C)]
pub struct AddressLengthParams {
    pub addr: u64,
    pub length: u64,
}

#[derive(Default, SchemaRead, FromBytes)]
#[repr(C)]
pub struct PacketLenParams {
    pub write_pkt_len: u32,
    pub read_pkt_len: u32,
}

#[derive(Default, SchemaWrite, ToBytes)]
#[repr(C)]
pub struct FlashOpParams {
    pub storage_type: u32,
    pub partition_type: u32,
    pub addr: u64,
    pub size: u64,
    pub nand_param: [u8; 32],
}

#[derive(SchemaWrite, ToBytes)]
pub struct EnvParams {
    pub da_log_level: u32,
    pub log_channel: u32,
    pub system_os: u32,
    pub ufs_provision: u32,
    pub reserved: u32,
}

#[derive(SchemaWrite, ToBytes)]
pub struct RebootParams {
    /// If set, the device will reboot into the
    /// specified bootup mode.
    pub is_dev_reboot: u32,
    /// WDT timeout
    pub timeout_ms: u32,
    pub async_flag: u32,
    /// The boot mode (Normal, Fastboot...)
    pub bootup: u32,
    /// Whether the Download Bit is set or not,
    /// which will make the device enter download
    /// mode on the next boot if set.
    pub dlbit: u32,
    pub not_reset_rtc_time: u32,
    /// If set, the device will not disconnect the
    /// USB connection during reboot.
    pub not_disconnect_usb: u32,
}

#[derive(Default, SchemaRead, FromBytes)]
#[repr(C)]
pub struct SlaChallengeData {
    pub version: u32,
    pub rnd: [u8; 16],
    pub hrid: [u8; 16],
    pub soc_id: [u8; 32],
}

/* Extensions */

#[cfg(feature = "exploits")]
pub mod extensions {
    use super::*;

    #[derive(SchemaWrite, ToBytes)]
    #[repr(C)]
    pub struct ExtPointerTable {
        pub magic: u32,
        pub uart_base: u32,
        pub reg_devc: u32,
        pub malloc: u32,
        pub free: u32,
        pub mmc_get_card: u32,
    }

    #[repr(C)]
    #[derive(SchemaWrite, ToBytes)]
    pub struct DaCtx {
        pub sej_base: u32,
        pub tzcc_base: u32,
        pub da2_base: u32,
        pub da2_size: u32,
        pub write_pkt_len: u32,
        pub read_pkt_len: u32,
        pub storage_type: u32,
        pub usb_log: u32,
    }

    #[repr(C)]
    #[derive(SchemaWrite, ToBytes)]
    pub struct RpmbParams {
        pub start_sector: u32,
        pub sectors_count: u32,
    }
}
