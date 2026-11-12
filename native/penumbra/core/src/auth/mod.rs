/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
#[cfg(feature = "localslakeyring")]
mod keys;
#[cfg(feature = "localslakeyring")]
pub mod local_keyring;
mod sla;

pub use sla::{AuthManager, SignData, SignPurpose, SignRequest, Signer};
