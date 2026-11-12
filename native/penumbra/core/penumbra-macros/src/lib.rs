use proc_macro::TokenStream;

mod bytes;
mod xml_cmd;

#[proc_macro_derive(ToBytes)]
pub fn derive_to_bytes(input: TokenStream) -> TokenStream {
    bytes::derive_to_bytes(input)
}

#[proc_macro_derive(FromBytes)]
pub fn derive_from_bytes(input: TokenStream) -> TokenStream {
    bytes::derive_from_bytes(input)
}

/// Macro to derive XmlCommand trait for a struct
///
/// Usage:
/// #[derive(XmlCommand)]
/// #[xmlcmd(name = "EXT-SEJ", version = "2.0")]
/// struct ExtRunSej {
///     #[xml(tag = "anti_clone")]
///     anti_clone: String,
///     #[xml(tag = "data"), fmt = "0x{data:x}"]
///     data: u64,
///     #[xml(tag = "length"), fmt = "{length}"]
///     length: u32,
/// }
#[proc_macro_derive(XmlCommand, attributes(xmlcmd, xml))]
pub fn derive_xmlcmd(input: TokenStream) -> TokenStream {
    xml_cmd::xmlcmd_derive(input)
}
