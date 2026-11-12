/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
use hacc::DaEntry;
use log::{debug, info, warn};

use crate::Result;
use crate::exploit::{DaEntryExt, get_v6_payload};
use crate::utils::analysis::{Aarch64Analyzer, Analyzer, Arch, ArchAnalyzer, ArmAnalyzer};
use crate::utils::hash::hash;
use crate::utils::patching::*;

const EXT_LOADER: &[u8] = include_bytes!("../../../payloads/extloader_v6.bin");
const SLA_BYPASS: &[u8] = include_bytes!("../../../payloads/sla_xml.bin");
const FORCE_RETURN_ARM64: &[u8] = &[0x00, 0x00, 0x80, 0xD2, 0xC0, 0x03, 0x5F, 0xD6]; // mov x0, #0; ret
const FORCE_RETURN_ARM: &[u8] = &[0x00, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1]; // mov r0, #0; bx lr

pub fn patch_da(da: &mut DaEntry) -> Result<()> {
    patch_da2(da)?;
    patch_da1(da)?;
    Ok(())
}

pub fn patch_da1(da: &mut DaEntry) -> Result<()> {
    let Some(hash_pos) = da.hash_offset() else {
        warn!("Could not find DA1 hash position, skipping patching");
        return Ok(());
    };

    let hash_type = da.get_hash_type();
    let da2_code = da.da2_code();
    let hash_result = hash(hash_type, da2_code);

    debug!("New DA1 hash: {:X?}", hash_result);

    let da1_data = da.da1_data_mut();

    patch(da1_data, hash_pos, &hash_result)?;

    Ok(())
}

pub fn patch_da2(da: &mut DaEntry) -> Result<()> {
    let da2_addr = da.da2().addr() as u64;
    let da2_data = da.da2_data();

    debug!("Patching DA2 with VA: 0x{:08X}", da2_addr);

    let analyzer = match da.arch() {
        Arch::Arm => Analyzer::Arm(ArmAnalyzer::new(da2_data.into(), da2_addr)),
        Arch::Aarch64 => Analyzer::Aarch64(Aarch64Analyzer::new(da2_data.into(), da2_addr)),
        Arch::Thumb2 => unreachable!(),
    };

    let da2_data = da.da2_data_mut();

    patch_da_sla(da2_data, &analyzer)?;
    patch_boot_to(da2_data, &analyzer)?;
    patch_security(da2_data, &analyzer)?;

    Ok(())
}

pub fn patch_boot_to(da: &mut [u8], analyzer: &Analyzer) -> Result<bool> {
    if find_pattern(da, b"CMD:BOOT-TO", 0).is_some() {
        return Ok(true);
    }

    let is_arm64 = matches!(analyzer, Analyzer::Aarch64(_));

    let extloader = get_v6_payload(EXT_LOADER, is_arm64);

    let Some(rsc_func_off) = analyzer.fn_from_str("RSC file") else {
        warn!("Could not find RSC function to inject Ext-Loader!");
        return Ok(false);
    };

    debug!("Injecting Ext-Loader to DA2 at offset 0x{:X}", rsc_func_off);

    patch(da, rsc_func_off, extloader)?;
    patch_pattern_bytes(da, b"CMD:SET-RSC\0", b"CMD:BOOT-TO\0")?;

    info!("Injected Ext-Loader to DA2 successfully.");
    Ok(true)
}

fn patch_security(da: &mut [u8], analyzer: &Analyzer) -> Result<bool> {
    patch_sec_policy(da, analyzer)?;
    patch_sbc(da, analyzer)?;
    Ok(true)
}

fn patch_sec_policy(da: &mut [u8], analyzer: &Analyzer) -> Result<bool> {
    const POLICY_FUNC: &str = "==========security policy==========";

    let get_policy = analyzer
        .fn_from_str(POLICY_FUNC)
        .and_then(|off| analyzer.next_bl_from_off(off))
        .and_then(|off| analyzer.next_bl_from_off(off + 4))
        .and_then(|bl| analyzer.bl_target_off(bl))
        .and_then(|off| analyzer.next_bl_from_off(off))
        .and_then(|off| analyzer.bl_target_off(off));

    if get_policy.is_none() {
        warn!("Could not find get_policy function to patch!");
        return Ok(false);
    }

    let return_zero = if matches!(analyzer, Analyzer::Aarch64(_)) {
        FORCE_RETURN_ARM64
    } else {
        FORCE_RETURN_ARM
    };

    debug!("Patching get_policy function at offset 0x{:X}", get_policy.unwrap());

    patch(da, get_policy.unwrap(), return_zero)?;

    info!("Patched DA2 security policy!");

    Ok(true)
}

fn patch_sbc(da: &mut [u8], analyzer: &Analyzer) -> Result<bool> {
    const SBC_FUNC: &str = "[SBC] sbc_en = %d\n";

    let get_sbc = analyzer
        .fn_from_str(SBC_FUNC)
        .and_then(|off| analyzer.next_bl_from_off(off))
        .and_then(|off| analyzer.next_bl_from_off(off + 4))
        .and_then(|off| analyzer.bl_target_off(off));

    if get_sbc.is_none() {
        warn!("Could not find SBC function to patch!");
        return Ok(false);
    }

    let return_zero = if matches!(analyzer, Analyzer::Aarch64(_)) {
        FORCE_RETURN_ARM64
    } else {
        FORCE_RETURN_ARM
    };

    debug!("Patching get_sbc function at offset 0x{:X}", get_sbc.unwrap());

    patch(da, get_sbc.unwrap(), return_zero)?;

    info!("Patched SBC to be disabled!");

    Ok(true)
}

fn patch_da_sla(da: &mut [u8], analyzer: &Analyzer) -> Result<bool> {
    const DOWNLOAD_MAGIC: u32 = 0x53434D44;
    const CMDS_MAGIC: u32 = 0x53434D45;

    // 情形一：DA 里没有 `DA.SLA\0ENABLED` 标记，说明它就没开 SLA 校验，
    // 无需替换 payload，属于「无需补丁」而不是「补丁失败」。
    if find_pattern(da, b"DA.SLA\0ENABLED", 0).is_none() {
        debug!("[Patch] 跳过 DA SLA：这份 DA 没有 `DA.SLA\\0ENABLED` 标记，无需处理。");
        return Ok(false);
    }

    let sla_func = analyzer.fn_from_str("SEC-POLICY");
    let download_ptr =
        analyzer.fn_from_str("Download host file:%s").and_then(|f| analyzer.off_to_va(f));
    let reg_sec_cmds_ptr = analyzer.fn_from_str("CMD:REBOOT").and_then(|f| analyzer.off_to_va(f));

    // 情形二：标记在，但定位 payload 所需的锚点缺了。这是真的没打上补丁，需要显式报出来。
    if sla_func.is_none() || download_ptr.is_none() || reg_sec_cmds_ptr.is_none() {
        warn!(
            "[Patch] DA SLA 未打补丁：锚点缺失（缺 SEC-POLICY / Download host file / CMD:REBOOT 中的某个），DA 结构可能不匹配。"
        );
        return Ok(false);
    }

    let sla_func = sla_func.unwrap();
    let download_ptr = download_ptr.unwrap() as u32;
    let reg_sec_cmds_ptr = reg_sec_cmds_ptr.unwrap() as u32;

    debug!(
        "Patching DA SLA at offset 0x{:X}, download_ptr: 0x{:X}, reg_sec_cmds_ptr: 0x{:X}",
        sla_func, download_ptr, reg_sec_cmds_ptr
    );

    let is_64bit = matches!(analyzer, Analyzer::Aarch64(_));

    let mut payload = get_v6_payload(SLA_BYPASS, is_64bit).to_vec();

    patch_u32(&mut payload, DOWNLOAD_MAGIC, download_ptr)?;
    patch_u32(&mut payload, CMDS_MAGIC, reg_sec_cmds_ptr)?;
    patch(da, sla_func, &payload)?;

    info!("Patched DA SLA to accepy dummy auth.");

    Ok(true)
}

