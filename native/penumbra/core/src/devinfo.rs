/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/

use std::cell::RefCell;
use std::rc::Rc;

use acon::{MMIO, SoC};
use hacc::BootControl;

use crate::Partition;

#[derive(Clone, Default)]
pub struct DevInfo {
    inner: Rc<RefCell<DevInfoData>>,
}

#[derive(Default, Clone)]
pub struct DevInfoData {
    pub soc_id: [u8; 32],
    pub meid: [u8; 16],
    pub hw_code: u16,
    pub hw_subcode: u16,
    pub chip: Option<SoC>,
    pub partitions: Vec<Partition>,
    pub bootctrl: Option<BootControl>,
    pub target_config: u32,
}

impl DevInfo {
    pub fn new(data: DevInfoData) -> Self {
        Self { inner: Rc::new(RefCell::new(data)) }
    }

    pub fn data(&self) -> DevInfoData {
        self.inner.borrow().clone()
    }

    pub fn set_data(&self, data: DevInfoData) {
        *self.inner.borrow_mut() = data;
    }

    pub fn soc_id(&self) -> [u8; 32] {
        self.inner.borrow().soc_id
    }

    pub fn set_soc_id(&self, soc_id: [u8; 32]) {
        self.inner.borrow_mut().soc_id = soc_id;
    }

    pub fn meid(&self) -> [u8; 16] {
        self.inner.borrow().meid
    }

    pub fn set_meid(&self, meid: [u8; 16]) {
        self.inner.borrow_mut().meid = meid;
    }

    pub fn hw_subcode(&self) -> u16 {
        self.inner.borrow().hw_subcode
    }

    pub fn set_hw_subcode(&self, hw_subcode: u16) {
        self.inner.borrow_mut().hw_subcode = hw_subcode;
    }

    pub fn chip(&self) -> Option<SoC> {
        self.inner.borrow().chip
    }

    pub fn set_chip(&self, chip: SoC) {
        self.inner.borrow_mut().chip = Some(chip);
    }

    pub fn clear_chip(&self) {
        self.inner.borrow_mut().chip = None;
    }

    pub fn hw_code(&self) -> u16 {
        // Prefer the chip's hwcode if available, otherwise fall back to the stored hw_code.
        self.chip().map(|c| c.to_hwcode()).unwrap_or(self.inner.borrow().hw_code)
    }

    pub fn partitions(&self) -> Vec<Partition> {
        self.inner.borrow().partitions.clone()
    }

    pub fn set_partitions(&self, partitions: Vec<Partition>) {
        self.inner.borrow_mut().partitions = partitions;
    }

    pub fn get_partition(&self, name: &str) -> Option<Partition> {
        let data = self.inner.borrow();

        if let Some(p) = data.partitions.iter().find(|p| p.name.eq_ignore_ascii_case(name)) {
            return Some(p.clone());
        }

        let suffix = data.bootctrl.as_ref()?.get_current_suffix().map(|s| s.to_string())?;

        let suffixed_name = format!("{name}{suffix}");

        data.partitions.iter().find(|p| p.name.eq_ignore_ascii_case(&suffixed_name)).cloned()
    }

    pub fn bootctrl(&self) -> Option<BootControl> {
        self.inner.borrow().bootctrl.clone()
    }

    pub fn set_bootctrl(&self, bootctrl: BootControl) {
        self.inner.borrow_mut().bootctrl = Some(bootctrl);
    }

    pub fn target_config(&self) -> u32 {
        self.inner.borrow().target_config
    }

    pub fn set_target_config(&self, cfg: u32) {
        self.inner.borrow_mut().target_config = cfg;
    }

    pub fn sbc_enabled(&self) -> bool {
        (self.target_config() & 0x1) != 0
    }

    pub fn sla_enabled(&self) -> bool {
        (self.target_config() & 0x2) != 0
    }

    pub fn daa_enabled(&self) -> bool {
        (self.target_config() & 0x4) != 0
    }
}
