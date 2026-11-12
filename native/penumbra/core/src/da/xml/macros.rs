/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

/// Constructs a DA XML cmd with positional arguments,
/// and sends it.
/// If `None` is provided, and a default value exists, the default is used.
macro_rules! xmlcmd {
    ($self:expr, $port:expr, $cmd_ty:ty $(, $arg:expr )* $(,)?) => {{
        let cmd = <$cmd_ty>::new( $( $arg ),* );
        $self.send_cmd($port, &cmd)
    }};
}

/// Constructs a DA XML cmd with positional arguments,
/// sends it, and then aknowledges CMD:END
macro_rules! xmlcmd_e {
    ($self:expr, $port:expr, $cmd_ty:ty $(, $arg:expr )* $(,)?) => {{
        let cmd = <$cmd_ty>::new( $( $arg ),* );
        $self.send_cmd($port, &cmd).and_then(|_| {
            $self.lifetime_ack($port, crate::da::xml::cmd::XmlCmdLifetime::CmdEnd)
        })
    }};
}

macro_rules! xmlcmd_p {
    ($self:expr, $port:expr, $cmd_ty:ty $(, $arg:expr )* $(,)?) => {{
        let cmd = <$cmd_ty>::new( $( $arg ),* );
        $self.send_cmd($port, &cmd).and_then(|_| {
            $self.progress_report($port, 0, crate::da::NOOP_PROGRESS).and_then(|_| {
            $self.lifetime_ack($port, crate::da::xml::cmd::XmlCmdLifetime::CmdEnd)
            })
        })
    }};
}
