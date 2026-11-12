/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2026 Shomy
*/

use std::fmt::{Display, Formatter};
use std::sync::{Arc, Mutex, PoisonError};

pub type OnActivity = Box<dyn Fn(&Activity) + Send + Sync>;

#[derive(Debug, Clone, PartialEq, Eq, Default)]
#[non_exhaustive]
pub enum Activity {
    #[default]
    Idle,
    UploadingDa,
    Flashing {
        partition: String,
    },
    Reading {
        partition: String,
    },
    Erasing {
        partition: String,
    },
}

impl Display for Activity {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Idle => write!(f, "Idle"),
            Self::UploadingDa => write!(f, "Uploading DA"),
            Self::Flashing { partition } => write!(f, "Flashing partition: {}", partition),
            Self::Reading { partition } => write!(f, "Reading partition: {}", partition),
            Self::Erasing { partition } => write!(f, "Erasing partition: {}", partition),
        }
    }
}

#[derive(Default)]
struct Inner {
    current: Mutex<Activity>,
    on_change: Option<OnActivity>,
}

#[derive(Clone, Default)]
pub struct DeviceActivity {
    inner: Arc<Inner>,
}

impl DeviceActivity {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_on_change(on_change: OnActivity) -> Self {
        Self {
            inner: Arc::new(Inner {
                current: Mutex::new(Activity::Idle),
                on_change: Some(on_change),
            }),
        }
    }

    pub fn current(&self) -> Activity {
        self.inner.current.lock().unwrap_or_else(PoisonError::into_inner).clone()
    }

    pub(crate) fn set(&self, activity: Activity) {
        {
            let mut current = self.inner.current.lock().unwrap_or_else(PoisonError::into_inner);
            if *current == activity {
                return;
            }
            *current = activity.clone();
        }

        if let Some(cb) = self.inner.on_change.as_ref() {
            cb(&activity);
        }
    }
}
