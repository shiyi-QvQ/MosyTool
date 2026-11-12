/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

use crate::traits::ToBytes;

/// Commands for XFlash protocol.
/// XFlash commands are categorised in 5 groups:
/// - 0x01xxxx: Main commands (Also known as Major commands)
/// - 0x02xxxx: Set commands
/// - 0x04xxxx: Get commands
/// - 0x08xxxx: Action commands
/// - 0x0Exxxx: Control commands
///
/// Extensions are penumbra specific commands, not part of the original
/// protocol, and are categorised in 0x0Fxxxx range.
#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(dead_code)]
pub enum Cmd {
    SyncSignal = 0x434E5953,

    Unknown = 0x010000,
    Download = 0x010001,
    Upload = 0x010002,
    Format = 0x010003,
    WriteData = 0x010004,
    ReadData = 0x010005,
    FormatPartition = 0x010006,
    Shutdown = 0x010007,
    BootTo = 0x010008,
    DeviceCtrl = 0x010009,
    InitExtRam = 0x01000A,
    SwitchUsbSpeed = 0x01000B,
    ReadOtpZone = 0x01000C,
    WriteOtpZone = 0x01000D,
    WriteEfuse = 0x01000E,
    ReadEfuse = 0x01000F,
    NandBmtRemark = 0x010010,
    SramWriteTest = 0x010011,

    SetupEnvironment = 0x010100,
    SetupHwInitParams = 0x010101,

    SetBmtPercentage = 0x020001,
    SetBatteryOpt = 0x020002,
    SetChecksumLevel = 0x020003,
    SetResetKey = 0x020004,
    SetHostInfo = 0x020005,
    SetMetaBootMode = 0x020006,
    SetEmmcHwresetPin = 0x020007,
    SetGenerateGpx = 0x020008,
    SetRegisterValue = 0x020009,
    SetExternalSig = 0x02000A,
    SetRemoteSecPolicy = 0x02000B,
    SetAllInOneSig = 0x02000C,
    SetRscInfo = 0x02000D,
    SetRebootMode = 0x02000E,
    SetCertFile = 0x02000F,
    SetUpdateFw = 0x020010,
    SetUfsConfig = 0x020011,
    SetDynamicPartMap = 0x020012,

    GetEmmcInfo = 0x040001,
    GetNandInfo = 0x040002,
    GetNorInfo = 0x040003,
    GetUfsInfo = 0x040004,
    GetDaVersion = 0x040005,
    GetExpireData = 0x040006,
    GetPacketLength = 0x040007,
    GetRandomId = 0x040008,
    GetPartitionTblCata = 0x040009,
    GetConnectionAgent = 0x04000A,
    GetUsbSpeed = 0x04000B,
    GetRamInfo = 0x04000C,
    GetChipId = 0x04000D,
    GetOtpLockStatus = 0x04000E,
    GetBatteryVoltage = 0x04000F,
    GetRpmbStatus = 0x040010,
    GetExpireDate = 0x040011,
    GetDramType = 0x040012,
    GetDevFwInfo = 0x040013,
    GetHrid = 0x040014,
    GetErrorDetail = 0x040015,
    SlaEnabledStatus = 0x040016,

    StartDlInfo = 0x080001,
    EndDlInfo = 0x080002,
    ActLockOtpZone = 0x080003,
    DisableEmmcHwresetPin = 0x080004,
    CcOptionalDownloadAct = 0x080005,
    DaStorLifeCycleCheck = 0x080007,
    DisableSparseErase = 0x080008,

    UnknownCtrlCode = 0x0E0000,
    CtrlStorageTest = 0x0E0001,
    CtrlRamTest = 0x0E0002,
    DeviceCtrlReadRegister = 0x0E0003,

    // Extensions
    ExtAck = 0x0F0000,
    ExtSetupDaCtx = 0x0F0001,
    ExtReadMem = 0x0F0002,
    ExtWriteMem = 0x0F0003,
    ExtReadRegister = 0x0F0004,
    ExtWriteRegister = 0x0F0005,
    ExtKeyDerive = 0x0F0006,
    ExtSej = 0x0F0007,
    ExtRpmbInit = 0x0F0008,
    ExtRpmbRead = 0x0F0009,
    ExtRpmbWrite = 0x0F000A,
}

impl ToBytes for Cmd {
    type Output = [u8; 4];

    const SIZE: usize = size_of::<u32>();

    fn to_bytes(&self) -> Self::Output {
        let cmd = *self as u32;
        cmd.to_le_bytes()
    }
}
