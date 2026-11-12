/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
use memchr::memmem;
use num_bigint::BigUint;

use super::keys::SLA_KEYS;
use super::{SignRequest, Signer};
use crate::SignPurpose;
use crate::error::{AuthError, Result};
use crate::utils::rsa::{RsaPrivateKey, rsa_oaep_encrypt, rsa_pkcs1_encrypt};

pub struct LocalKeyring {
    /// The private key used for signing.
    /// In MTK SLA flow, signing is performed by encrypting data with the private key.
    /// The corresponding public key is used for verification.
    keys: Vec<RsaPrivateKey>,
}

impl Signer for LocalKeyring {
    fn sign(&self, req: &SignRequest) -> Result<Vec<u8>> {
        let key = self
            .keys
            .iter()
            .find(|k| memmem::find(&req.pubk_mod, &k.n().to_bytes_be()).is_some())
            .ok_or(AuthError::NoMatchingKeyFound)?;

        let signature = match req.purpose {
            SignPurpose::BromSla => rsa_pkcs1_encrypt(&req.data.raw, key.n(), key.d()),
            SignPurpose::DaSla => rsa_oaep_encrypt(&req.data.rnd, key.n(), key.d()),
            _ => return Err(AuthError::PurposeNotSupported.into()),
        };

        Ok(signature)
    }

    fn can_handle(&self, pubk_mod: &[u8]) -> bool {
        self.keys.iter().any(|k| memmem::find(pubk_mod, &k.n().to_bytes_be()).is_some())
    }

    fn is_authorized(&self, _req: &SignRequest) -> bool {
        true
    }
}

impl LocalKeyring {
    pub fn new() -> Self {
        let keys = SLA_KEYS
            .iter()
            .map(|raw_key| {
                // It's fine here to panic on invalid keys, since these are hardcoded, so in case
                // of an error, we want to catch it :)
                let n = BigUint::parse_bytes(raw_key.n.as_bytes(), 16).expect("Invalid hex in n");
                let d = BigUint::parse_bytes(raw_key.d.as_bytes(), 16).expect("Invalid hex in d");

                RsaPrivateKey::new(n, d)
            })
            .collect();

        Self { keys }
    }
}

impl Default for LocalKeyring {
    fn default() -> Self {
        Self::new()
    }
}
