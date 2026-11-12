/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/

use std::fmt::Debug;
use std::sync::{Arc, Mutex};

pub type OnPush = Box<dyn Fn(&str) + Send + Sync>;

#[derive(Clone)]
pub struct DeviceLog {
    inner: Arc<Inner>,
}

struct Inner {
    entries: Mutex<Vec<String>>,
    /// DA 启动早期打印的分区表（PMT）原始消息，单独留一份。
    ///
    /// 不能只靠 `entries`：它触顶时丢的是**前一半**，而 PMT 恰好是最早那批消息，
    /// 设备一旦进入异常刷屏（「I'm alive.」之类）就会被冲走。而 SLA 未通过时
    /// 这份 PMT 是唯一还能拿到的分区表来源，丢了就真没了。
    pmt: Mutex<Vec<String>>,
    on_push: Option<OnPush>,
}

impl Debug for DeviceLog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DeviceLog")
            .field("entries", &self.inner.entries)
            .field("on_push", &self.inner.on_push.as_ref().map(|_| ".."))
            .finish()
    }
}

impl Default for DeviceLog {
    fn default() -> Self {
        Self::new()
    }
}

impl DeviceLog {
    /// 缓冲的条数上限。
    ///
    /// USB DA 日志通道打开时，设备会持续往这个缓冲里塞消息，而目前没有任何消费者会
    /// `drain()` 它。不设上限的话，长时间刷机（或设备异常狂刷）会一路吃掉内存，直到进程被拖垮。
    /// 触顶后一次性丢掉前一半：摊还到每条 push 仍是 O(1)，不会退化成「每来一条就搬一次整个 Vec」。
    const MAX_ENTRIES: usize = 8192;

    /// PMT 旁路缓冲的条数上限。
    ///
    /// 一台设备的 PMT 通常几十条，1024 已经非常宽裕。给上限只是为了防止畸形设备
    /// 拿假 PMT 行把内存灌满——毕竟这份缓冲是不截断的。
    const MAX_PMT_ENTRIES: usize = 1024;

    pub fn new() -> Self {
        Self {
            inner: Arc::new(Inner {
                entries: Mutex::new(Vec::new()),
                pmt: Mutex::new(Vec::new()),
                on_push: None,
            }),
        }
    }

    pub fn with_on_push(on_push: OnPush) -> Self {
        Self {
            inner: Arc::new(Inner {
                entries: Mutex::new(Vec::new()),
                pmt: Mutex::new(Vec::new()),
                on_push: Some(on_push),
            }),
        }
    }

    pub fn push(&self, message: String) {
        if let Some(ref cb) = self.inner.on_push {
            cb(&message);
        }

        // 先把 PMT 抢下来，再进会被截断的主缓冲。
        if contains_partition_line(&message)
            && let Ok(mut pmt) = self.inner.pmt.lock()
            && pmt.len() < Self::MAX_PMT_ENTRIES
        {
            pmt.push(message.clone());
        }

        if let Ok(mut entries) = self.inner.entries.lock() {
            if entries.len() >= Self::MAX_ENTRIES {
                entries.drain(..Self::MAX_ENTRIES / 2);
            }
            entries.push(message);
        }
    }

    /// 返回捕获到的分区表（PMT）原始消息。
    ///
    /// 与 [`Self::entries`] 不同，这份内容不会被主缓冲的截断策略波及。
    pub fn partition_lines(&self) -> Vec<String> {
        self.inner.pmt.lock().map(|p| p.clone()).unwrap_or_default()
    }

    pub fn entries(&self) -> Vec<String> {
        self.inner.entries.lock().map(|e| e.clone()).unwrap_or_default()
    }

    pub fn drain(&self) -> Vec<String> {
        self.inner.entries.lock().map(|mut e| std::mem::take(&mut *e)).unwrap_or_default()
    }

    pub fn len(&self) -> usize {
        self.inner.entries.lock().map(|e| e.len()).unwrap_or(0)
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    pub fn clear(&self) {
        if let Ok(mut entries) = self.inner.entries.lock() {
            entries.clear();
        }
    }
}

/// 判断一条 DA 日志消息里是否含分区表（PMT）记录。
pub(crate) fn contains_partition_line(message: &str) -> bool {
    message.lines().any(|line| parse_partition_line(line).is_some())
}

/// 解析单条 PMT 记录：`[name] At[0xADDR] Size[0xSIZE]`。
///
/// 返回 `(名字, 起始地址, 大小)`。这是 DA 自带的分区表打印，发生在 SLA 校验之前，
/// 因此不受任何授权门限制。
pub(crate) fn parse_partition_line(line: &str) -> Option<(&str, u64, u64)> {
    let rest = line.trim().strip_prefix('[')?;
    let close = rest.find(']')?;

    let name = &rest[..close];
    if name.is_empty() {
        return None;
    }

    let rest = rest[close + 1..].trim_start().strip_prefix("At[")?;
    let close = rest.find(']')?;
    let address = parse_hex(&rest[..close])?;

    let rest = rest[close + 1..].trim_start().strip_prefix("Size[")?;
    let close = rest.find(']')?;
    let size = parse_hex(&rest[..close])?;

    Some((name, address, size))
}

fn parse_hex(raw: &str) -> Option<u64> {
    let raw = raw.trim();
    let digits = raw.strip_prefix("0x").or_else(|| raw.strip_prefix("0X")).unwrap_or(raw);

    if digits.is_empty() {
        return None;
    }

    u64::from_str_radix(digits, 16).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 取自真机日志（MT6991）：DA 在 SLA 校验之前打印的 PMT。
    #[test]
    fn parses_real_pmt_line() {
        let (name, address, size) =
            parse_partition_line("[vendor_boot_a] At[0x59a00000] Size[0x6000000]").unwrap();

        assert_eq!(name, "vendor_boot_a");
        assert_eq!(address, 0x59a0_0000);
        assert_eq!(size, 0x600_0000);
    }

    /// userdata 那条：长度 10 位十六进制，别在解析里被截断。
    #[test]
    fn parses_wide_addresses() {
        let (name, address, size) =
            parse_partition_line("[userdata] At[0x44f800000] Size[0x37357f8000]").unwrap();

        assert_eq!(name, "userdata");
        assert_eq!(address, 0x4_4f80_0000);
        assert_eq!(size, 0x37_357f_8000);
    }

    /// DA 日志里绝大多数行都是噪声，必须一条都不能误判成分区。
    #[test]
    fn ignores_noise() {
        for line in [
            "I'm alive.",
            "[PMIC]IsUsbCableIn 1",
            "***setup_security_env.***",
            "[DA.SLA]ENABLED",
            "oplus_version:MT6991_V3.3",
            "",
            "[](empty name) At[0x0] Size[0x0]",
            "[At[0x0] no size]",
            "[only_name]",
        ] {
            assert!(
                parse_partition_line(line).is_none(),
                "不该把 `{line}` 判成分区记录"
            );
        }
    }

    /// 整张表可能挤在一条消息里，按行切分要能全抓出来。
    #[test]
    fn scans_multi_line_message() {
        let message = "[super] At[0xe4800000] Size[0x36b000000]\n\
                       [userdata] At[0x44f800000] Size[0x37357f8000]\n\
                       I'm alive.";

        assert!(contains_partition_line(message));

        let found: Vec<_> = message
            .lines()
            .filter_map(parse_partition_line)
            .map(|(name, _, _)| name)
            .collect();

        assert_eq!(found, ["super", "userdata"]);
    }
}
