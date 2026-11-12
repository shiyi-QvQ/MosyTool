/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/
use std::collections::HashSet;

use crate::da::NOOP_PROGRESS;
use crate::log_buffer::parse_partition_line;
use crate::storage::gpt::{GPT_SIZE, MAX_GPT_PARTS};
use crate::{
    DeviceLog,
    DownloadProtocol,
    Gpt,
    MtkPort,
    Partition,
    PartitionKind,
    Storage,
    StorageKind,
};

pub(super) fn get_aux_gpt_parts(storage: &StorageKind) -> [Partition; 2] {
    let pl1_size = storage.get_pl1_size() as usize;
    let pl1_part = storage.get_pl_part1();
    let pl2_size = storage.get_pl2_size() as usize;
    let pl2_part = storage.get_pl_part2();

    [
        Partition::new("preloader", pl1_size, 0, pl1_part),
        Partition::new("preloader_backup", pl2_size, 0, pl2_part),
    ]
}

pub(super) fn get_gpt_parts<P: MtkPort, D: DownloadProtocol>(
    proto: &mut D,
    port: &mut P,
    storage: &StorageKind,
) -> Vec<Partition> {
    let user_section = storage.get_user_part();
    let user_size = storage.get_user_size();

    let gpt_size = GPT_SIZE;

    let mut gpt_parts = Vec::with_capacity(MAX_GPT_PARTS + 2);

    let pgpt = Partition::new("PGPT", gpt_size, 0, user_section);

    let sgpt = Partition::new("SGPT", gpt_size, user_size - gpt_size as u64, user_section);

    gpt_parts.push(pgpt);

    for gpt_name in ["PGPT", "SGPT"] {
        let mut data = Vec::new();

        if proto.read_partition(port, gpt_name, &mut data, NOOP_PROGRESS).is_ok()
            && let Ok(gpt) = Gpt::from_bytes(&data)
        {
            let mut parsed = gpt.to_partitions(storage);
            if !parsed.is_empty() {
                gpt_parts.append(&mut parsed);
                break;
            }
        }
    }

    gpt_parts.push(sgpt);

    gpt_parts
}

/// 从 DA 日志通道捕获的 PMT 记录解析分区表。
///
/// 真读 GPT（`read_partition(port, "PGPT")`）用的是 `CMD:READ-PARTITION` 一类的命令，
/// 属于受 SLA 保护的操作——SLA 没通过时它必然失败，分区表也就必然是空的。
/// 但 DA 在建立 SLA 门**之前**就把整张 PMT 打进了日志通道。这条路径不碰任何受保护的
/// 命令，因此是 SLA 未通过时唯一还能拿到的分区表来源。
///
/// 同名分区只保留第一次出现。PMT 里 A/B 槽位的名字本来就不同，出现重名通常意味着
/// 日志里打了不止一份表，去重可以避免把同一分区算两次。
pub(super) fn get_log_parts(log: &DeviceLog, storage: &StorageKind) -> Vec<Partition> {
    parse_log_parts(log, storage.get_user_part())
}

/// 存储类型未知时，仍然从日志 PMT 恢复分区表。
///
/// 相比 [`get_log_parts`]，这里唯一缺的是「分区落在哪个 LUN」（[`PartitionKind`]）。
/// 而这个字段在实际动作里用不上：按名字读/写分区走的是 `CMD:READ-PARTITION` /
/// `CMD:WRITE-PARTITION`，只带分区名，由 DA 自己去查表（见 `da::xml::flash`）。
/// 所以标成 `Unknown` 即可——总好过因为探测不出存储类型，就把 DA 明明已经
/// 打印出来的整张表丢掉。
pub(super) fn get_log_parts_without_storage(log: &DeviceLog) -> Vec<Partition> {
    parse_log_parts(log, PartitionKind::Unknown)
}

fn parse_log_parts(log: &DeviceLog, user_section: PartitionKind) -> Vec<Partition> {
    let lines = log.partition_lines();

    let mut parts = Vec::new();
    let mut seen: HashSet<&str> = HashSet::new();

    for message in &lines {
        for line in message.lines() {
            let Some((name, address, size)) = parse_partition_line(line) else {
                continue;
            };

            if seen.insert(name) {
                parts.push(Partition::new(name, size as usize, address, user_section));
            }
        }
    }

    parts
}
