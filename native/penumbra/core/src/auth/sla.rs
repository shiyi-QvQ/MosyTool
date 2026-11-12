/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/
use std::fmt::Debug;
use std::sync::{Arc, OnceLock, RwLock};

#[cfg(feature = "localslakeyring")]
use crate::auth::local_keyring::LocalKeyring;
use crate::error::{AuthError, Result};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum SignPurpose {
    BromSla,
    PlSla,
    MetaSla,
    DaSla,
}

impl Debug for SignPurpose {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::BromSla => write!(f, "Brom SLA"),
            Self::PlSla => write!(f, "Preloader SLA"),
            Self::MetaSla => write!(f, "Meta SLA"),
            Self::DaSla => write!(f, "DA SLA"),
        }
    }
}

#[derive(Debug, Default)]
pub struct SignData {
    pub rnd: Vec<u8>,
    pub soc_id: Vec<u8>,
    pub hrid: Vec<u8>,
    pub raw: Vec<u8>,
}

pub struct SignRequest {
    pub data: SignData,
    pub purpose: SignPurpose,
    pub pubk_mod: Vec<u8>,
}

pub trait Signer: Send + Sync {
    /// Whether the signer can handle a a sign request,
    /// for example, if it matches the public key
    fn can_handle(&self, pubk_mod: &[u8]) -> bool;
    /// Whether the signer authorizes a sign request to be signed
    /// at all. For example, if a device is banned or restricted.
    fn is_authorized(&self, req: &SignRequest) -> bool;
    /// Signs the SLA challenge and returns the signed data
    fn sign(&self, req: &SignRequest) -> Result<Vec<u8>>;
}

pub struct AuthManager {
    signers: RwLock<Vec<Arc<dyn Signer>>>,
}

static INSTANCE: OnceLock<AuthManager> = OnceLock::new();

impl AuthManager {
    /// Get the global AuthManager instance.
    pub fn get() -> &'static Self {
        INSTANCE.get_or_init(|| {
            #[allow(unused_mut)]
            let mut default_signers: Vec<Arc<dyn Signer>> = Vec::new();

            #[cfg(feature = "localslakeyring")]
            {
                let local_keyring = Arc::new(LocalKeyring::new());
                default_signers.push(local_keyring);
            }

            Self { signers: RwLock::new(default_signers) }
        })
    }

    /// Registers a new signer to be available for signing requests.
    pub fn register_signer(&self, signer: Arc<dyn Signer>) -> Result<()> {
        self.signers.write().unwrap().push(signer);

        Ok(())
    }

    /// Return whether any of the registered signers can sign the given request.
    pub fn can_sign(&self, pubk: &[u8]) -> bool {
        let Ok(signers) = self.signers.read() else {
            return false;
        };

        for signer in signers.iter() {
            if signer.can_handle(pubk) {
                return true;
            }
        }

        false
    }

    /// Signs the given request using the first capable signer.
    pub fn sign(&self, req: &SignRequest) -> Result<Vec<u8>> {
        let signers = {
            let list = self.signers.read().unwrap();
            list.clone()
        };

        for signer in signers {
            if signer.can_handle(&req.pubk_mod) && signer.is_authorized(req) {
                return signer.sign(req);
            }
        }

        Err(AuthError::NoSignerAvailable.into())
    }
}
