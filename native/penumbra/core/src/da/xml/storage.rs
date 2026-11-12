/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
use log::debug;

use crate::da::xml::Xml;
use crate::da::xml::cmd::{GetHwInfo, XmlCmdLifetime};
use crate::error::{ProtocolError, Result};
use crate::port::MtkPort;
use crate::storage::{EmmcStorage, StorageKind, UfsStorage};
use crate::utils::xml::get_tag;

/// 探测设备的存储类型。
///
/// 这里**不能**把失败吞成 `None`：DA 不回应 GET-HW-INFO 时（例如被前一条命令卡住），
/// 调用方会拿到一个「没有分区」的空表，而主机侧无从分辨「设备真的没有分区」和「命令压根
/// 没被执行」——上层就会把失败当成功报。失败必须带着真实原因上抛。
pub fn detect_storage<P: MtkPort>(xml: &mut Xml, port: &mut P) -> Result<StorageKind> {
    xmlcmd!(xml, port, GetHwInfo)?;

    let response = xml.get_upload_file_resp(port)?;

    xml.lifetime_ack(port, XmlCmdLifetime::CmdEnd)?;
    let storage_str: String = get_tag(&response, "storage")?;

    match storage_str.as_str() {
        "EMMC" => {
            debug!("eMMC storage detected.");
            if let Ok(storage) = EmmcStorage::from_xml(&response) {
                return Ok(StorageKind::Emmc(storage));
            }
        }
        "UFS" => {
            debug!("UFS storage detected.");
            if let Ok(storage) = UfsStorage::from_xml(&response) {
                return Ok(StorageKind::Ufs(storage));
            }
        }
        _ => {}
    }

    Err(ProtocolError::CannotGetStorageInfo.into())
}
