/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DaLogLevel {
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

impl From<DaLogLevel> for &'static str {
    fn from(val: DaLogLevel) -> Self {
        match val {
            DaLogLevel::Trace => "TRACE",
            DaLogLevel::Debug => "DEBUG",
            DaLogLevel::Info => "INFO",
            DaLogLevel::Warning => "WARNING",
            DaLogLevel::Error => "ERROR",
            DaLogLevel::Fatal => "FATAL",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BootMode {
    Normal,
    HomeScreen,
    Fastboot,
    Test,
    Meta,
}

impl From<BootMode> for &'static str {
    fn from(val: BootMode) -> Self {
        match val {
            BootMode::Normal | BootMode::HomeScreen => "IMMEDIATE",
            BootMode::Fastboot => "FASTBOOT",
            BootMode::Test => "ANDROID-TEST-MODE",
            BootMode::Meta => "META",
        }
    }
}

#[cfg(feature = "exploits")]
pub mod extensions {
    use std::fmt::Display;

    use penumbra_macros::ToBytes;
    use wincode::SchemaWrite;

    #[derive(PartialEq, Eq)]
    pub enum SecCfgAlgo {
        /// Anti clone off
        Sha,
        /// Use SW key
        SW,
        /// Legacy HW encryption with XOR feedback
        HW,
        /// Legacy HW encryption with key feedback
        HWv3,
        /// New HW encryption, with KDF
        HWv4,
    }

    #[derive(PartialEq, Eq, SchemaWrite, ToBytes)]
    #[repr(u8)]
    #[wincode(tag_encoding = "u8")]
    pub enum KeySize {
        Key128 = 0,
        Key192 = 1,
        Key256 = 2,
    }

    impl Display for KeySize {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            match self {
                Self::Key128 => write!(f, "KEY_128"),
                Self::Key192 => write!(f, "KEY_192"),
                Self::Key256 => write!(f, "KEY_256"),
            }
        }
    }

    impl KeySize {
        #[inline]
        pub const fn to_bytes(&self) -> usize {
            match self {
                Self::Key128 => 16,
                Self::Key192 => 24,
                Self::Key256 => 32,
            }
        }
    }

    #[derive(SchemaWrite, ToBytes)]
    #[repr(u8)]
    #[wincode(tag_encoding = "u8")]
    pub enum SejKeyId {
        SwKey = 0,
        HwKey = 1,
        HwWrappedKey = 2,
        RidKey = 3,
        CustomKey = 4,
    }

    impl Display for SejKeyId {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            match self {
                Self::SwKey => write!(f, "SW_KEY"),
                Self::HwKey => write!(f, "HW_KEY"),
                Self::HwWrappedKey => write!(f, "HW_WRAPPED_KEY"),
                Self::RidKey => write!(f, "RID_KEY"),
                Self::CustomKey => write!(f, "CUSTOM_KEY"),
            }
        }
    }

    #[derive(SchemaWrite, ToBytes)]
    pub struct SejParams {
        /// Length of the data to encrypt.
        pub length: u32,
        /// Whether to encrypt or decrypt the data.
        pub encrypt: bool,
        /// Wether to use HW encryption or SW.
        pub anti_clone: bool,
        /// Used in Legacy HW encryption.
        pub xor: bool,
        /// Use legacy SEJ HW encryption
        pub legacy: bool,
        /// Whether to perform CBC or ECB encryption.
        pub cbc: bool,
        /// The key to use for encryption:
        /// 0: SW Key
        /// 1: HW Key
        /// 2: HW Wrapped Key
        /// 3: RID Key
        /// 4: Custom Key
        /// 5-255: Fallback to SW key
        /// When anti_clone is enabled, this will be
        /// ignored by SEJ
        pub key_id: SejKeyId,
        /// What key size to use:
        /// 0: 128-bit key
        /// 1: 192-bit key
        /// 2: 256-bit key
        pub key_sz: KeySize,
        pub reserved: u8,
    }

    impl Default for SejParams {
        fn default() -> Self {
            Self {
                length: 0,
                encrypt: false,
                anti_clone: false,
                xor: false,
                legacy: false,
                cbc: true,
                key_id: SejKeyId::SwKey,
                key_sz: KeySize::Key256,
                reserved: 0,
            }
        }
    }

    pub enum KeyDeriveParams<'a> {
        Id { id: KeyDeriveId, len: KeySize },
        Input { label: &'a [u8], salt: &'a [u8], len: KeySize },
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum KeyDeriveId {
        /// Rpmb Key
        Rpmb,
        /// Full Disk Encryption Key
        Fde,
        /// Tee decryption key
        Tee,
        /// Aes Image Encryption Key
        AesImgEnc,
        /// Customer Key for Aes Encryption
        AesCustom,
        /// Motorola RPMB Key
        Motorola,
        /// Root of Trust Key
        Rot,
        /// Input Key
        Input = 0xFF,
    }

    impl Display for KeyDeriveId {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            match self {
                Self::Rpmb => write!(f, "RPMB"),
                Self::Fde => write!(f, "FDE"),
                Self::Tee => write!(f, "TEE"),
                Self::AesImgEnc => write!(f, "AES_IMG_ENC"),
                Self::AesCustom => write!(f, "AES_CUSTOM"),
                Self::Motorola => write!(f, "MOTOROLA"),
                Self::Rot => write!(f, "ROT"),
                Self::Input => write!(f, "INPUT"),
            }
        }
    }
}
