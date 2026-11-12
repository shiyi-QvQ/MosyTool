/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/

use wincode::{Deserialize, SchemaRead, SchemaWrite};

use crate::traits::{FromBytes, ToBytes};

#[derive(Default, PartialEq, Eq, SchemaWrite, SchemaRead, FromBytes, ToBytes)]
#[repr(u32)]
#[wincode(tag_encoding = "u32")]
pub enum FlashUpdateStage {
    #[default]
    Stage0 = 0,
    Stage1 = 1,
    Stage2 = 2,
    Stage3 = 3,
}

#[derive(Default, PartialEq, Eq, SchemaWrite, SchemaRead, FromBytes, ToBytes)]
#[repr(u32)]
#[wincode(tag_encoding = "u32")]
pub enum FlashUpdateChanged {
    #[default]
    Unchanged = 0,
    Changed = 1,
    EmptyDev = 2,
}

#[derive(Default, PartialEq, Eq, SchemaWrite, SchemaRead, FromBytes, ToBytes)]
#[repr(u32)]
#[wincode(tag_encoding = "u32")]
pub enum PartitionChangedStatus {
    #[default]
    Unchanged = 0,
    ChangedAddr = 1,
    ChangedSize = 2,
}

#[derive(SchemaWrite, SchemaRead, ToBytes)]
#[repr(C)]
pub struct ProtectedSection {
    partition_name: [u8; 64],
    pub changed: PartitionChangedStatus,
    pub checksum: u32,
}

impl Default for ProtectedSection {
    fn default() -> Self {
        Self {
            partition_name: [0u8; 64],
            changed: PartitionChangedStatus::default(),
            checksum: u32::default(),
        }
    }
}

impl ProtectedSection {
    pub fn part_name(&self) -> &str {
        // Should never fail
        core::str::from_utf8(&self.partition_name).unwrap_or_default().trim_end_matches('\0')
    }
}

#[derive(Default, SchemaWrite, SchemaRead, FromBytes, ToBytes)]
#[repr(C)]
pub struct ProtectedRecord {
    pub count: u32,
    pub list: [ProtectedSection; 16],
    pub stage: FlashUpdateStage,
    pub changed: FlashUpdateChanged,
    pub gpt_changed: FlashUpdateChanged,
}
