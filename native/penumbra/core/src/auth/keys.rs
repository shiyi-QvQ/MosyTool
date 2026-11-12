/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025-2026 Shomy
*/

pub struct RawKey {
    #[allow(dead_code)]
    pub name: &'static str,
    pub n: &'static str,
    pub d: &'static str,
}

/// All keys for the Local SLA Keyring are defined here.
/// To add a new key, just make a new entry in the KEYS array below.
pub const SLA_KEYS: &[RawKey] = &[RawKey {
    name: "Motorola ODMs", // Motorola G24, G15, G05, E15, G06, G17
    n: "db8f46cf8da80af8cca1aec9ff7b358cfe4cc5659ade5ef9c196905caaf979658349284723bef9524532b21f460c0897468be95d0aa92682144d1bfcb84afc7712ff3b5dc34153e5efe64b465a6d8cf2bd8c2fb1bf27d9c77f26e90baa3ddada18525d3f689441ef7b6dc5c4b8c496b0a9c92f29d26dac8ff8b137d6a93cf26ad391bf6124207ff9eb26e10b65269c6bad38eff0c50aab604a0128b874f24263037c605bc9f855252f78173141d166b632dbb549370af71efdc522532cb55c48b9a39a21ee8e0cc8bb34c394aec92155a16f95b646aa9e5f88c989eaf2d7f615bf5afe619e27dfab5adbbd7999db9590ab0f30c95c98da39616cad6494be52b7",
    d: "a89df958cec69e5e82f12cc64f21b577a99916043912cc47ed278f88cb79ba847e7601abd8c502beef0bc706038a9c5269486c191b65da800dcd465028ccd5e530beb93e02053ac49d1ff4f17be3245b0bbd0ca7ea51558c439783648502e9ff92ac3696cadf09603d1f89c1d1d09095ee5ee68cace1b3a401ef401de86d3911ea96021dc5b5af36e6babf3d48d6a58a9075d5deeacadbfd09f93748929ef466a9d339d92370334e0e50afc0c43cfb1b9f2bff5e3b5a7012b93e92d8644f032993033245eec56d899837d1080c5aba7e09fe2e2eac06921159775392d64e819daa905a2931352ae02e3a21318b207e0fbfa113e8a32a37987243da2bb57d7d89",
}];
