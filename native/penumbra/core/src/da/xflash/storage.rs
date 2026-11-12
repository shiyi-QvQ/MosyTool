/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

use log::debug;

use crate::da::xflash::{Cmd, XFlash};
use crate::error::{ProtocolError, Result};
use crate::port::MtkPort;
use crate::storage::{EmmcStorage, StorageKind, UfsStorage};
use crate::traits::FromBytes;

// TODO: Avoid repeated logic
/// 探测设备的存储类型，失败时上抛真实错误（不要吞成 `None`，否则会变成无声的空分区表）。
pub fn detect_storage<P: MtkPort>(xflash: &mut XFlash, port: &mut P) -> Result<StorageKind> {
    let emmc_response = xflash.devctrl(port, Cmd::GetEmmcInfo, None);
    let ufs_response = xflash.devctrl(port, Cmd::GetUfsInfo, None);

    debug!("EMMC response: {:?}", emmc_response);
    debug!("UFS response: {:?}", ufs_response);

    if let Ok(ref resp) = emmc_response
        && let Some(storage) = EmmcStorage::from_bytes(resp)
    {
        debug!("eMMC storage detected.");
        return Ok(StorageKind::Emmc(storage));
    }

    if let Ok(ref resp) = ufs_response
        && let Some(storage) = UfsStorage::from_bytes(resp)
    {
        debug!("UFS storage detected.");
        return Ok(StorageKind::Ufs(storage));
    }

    // 两条探测命令都没给出可解析的响应：把真实错误交给上层，别静默变成「没有分区」。
    if let Err(e) = emmc_response {
        return Err(e);
    }
    if let Err(e) = ufs_response {
        return Err(e);
    }

    Err(ProtocolError::CannotGetStorageInfo.into())
}
