/*
    SPDX-License-Identifier: AGPL-3.0-or-later
    SPDX-FileCopyrightText: 2025 Shomy
*/
use proc_macro::TokenStream;
use proc_macro2::Span;
use quote::quote;
use regex::Regex;
use syn::{Attribute, Data, DeriveInput, Error, Fields, Ident, LitStr, parse_macro_input};

/// Represent a field's metadata for XML command generation
/// If no xml attribute is present, it's skipped and not added
/// to args, otherwise it's either Simple or Formatted (if fmt).
enum FieldMeta {
    Skip,
    Simple { tag: String, section: Option<String>, default_value: Option<String> },
    Formatted { tag: String, fmt: String, section: Option<String>, default_value: Option<String> },
}

/// Struct-level metadata extracted from #[xmlcmd(... )]
struct CmdMeta {
    name: String,
    version: String,
}

pub fn xmlcmd_derive(input: TokenStream) -> TokenStream {
    let parsed = parse_macro_input!(input as DeriveInput);
    let name = &parsed.ident;

    if !matches!(parsed.data, Data::Struct(_)) {
        return Error::new_spanned(name, "XmlCommand can only be derived for structs")
            .to_compile_error()
            .into();
    }

    let cmd_meta = extract_command_meta(&parsed.attrs, name);
    let cmd_name = &cmd_meta.name;
    let cmd_version = &cmd_meta.version;
    let arg_entries = extract_field_entries(&parsed.data);
    let (constructor_args, constructor_fields) = extract_constructor(&parsed.data);

    quote! {
        impl #name {
            pub fn new(
                #(#constructor_args),*
            ) -> Self {
                Self {
                    #(#constructor_fields),*
                }
            }
        }

        impl XmlCommand for #name {
            fn cmd_name(&self) -> &'static str {
                #cmd_name
            }

            fn version(&self) -> &'static str {
                #cmd_version
            }

            fn args(&self) -> Vec<(Option<&'static str>, &'static str, String)> {
                vec![
                    #(#arg_entries),*
                ]
            }
        }

        impl std::fmt::Display for #name {
             fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                 write!(f, "CMD:{}", self.cmd_name())?;

                 for (section, tag, content) in self.args() {
                     match section {
                         Some(sec) => write!(f, "\n  [{}] {}: {}", sec, tag, content)?,
                         None => write!(f, "\n  {}: {}", tag, content)?,
                     }
                 }

                 Ok(())
             }
         }
    }
    .into()
}

fn extract_command_meta(attrs: &[Attribute], name: &Ident) -> CmdMeta {
    let mut cmd_name = to_cmd_name(name);
    let mut version = "1.0".to_string();

    for attr in attrs {
        if !attr.path().is_ident("xmlcmd") {
            continue;
        }

        let _ = attr.parse_nested_meta(|meta| {
            let ident = match meta.path.get_ident() {
                Some(i) => i.to_string(),
                None => return Ok(()),
            };

            let lit: LitStr = meta.value()?.parse()?;

            match ident.as_str() {
                "name" => cmd_name = lit.value(),
                "version" => version = lit.value(),
                _ => {}
            }

            Ok(())
        });
    }

    CmdMeta { name: cmd_name, version }
}

fn extract_constructor(
    data: &Data,
) -> (Vec<proc_macro2::TokenStream>, Vec<proc_macro2::TokenStream>) {
    let mut constructor_args = Vec::new();
    let mut constructor_fields = Vec::new();

    if let Data::Struct(data_struct) = data
        && let Fields::Named(fields_named) = &data_struct.fields
    {
        for field in &fields_named.named {
            let ident = field.ident.as_ref().unwrap();
            let ty = &field.ty;

            let meta = extract_field_metadata(&field.attrs, ident);
            let default_value = match meta {
                FieldMeta::Simple { default_value, .. } => default_value,
                FieldMeta::Formatted { default_value, .. } => default_value,
                FieldMeta::Skip => None,
            };

            if let Some(val_str) = default_value {
                let val_tokens = if val_str == "true" || val_str == "false" {
                    val_str.parse::<proc_macro2::TokenStream>().unwrap()
                } else if val_str.starts_with("0x") || val_str.starts_with("0X") {
                    val_str
                        .parse::<proc_macro2::TokenStream>()
                        .unwrap_or_else(|_| quote! { #val_str })
                } else if val_str.chars().all(|c| c.is_ascii_digit()) {
                    val_str.parse::<proc_macro2::TokenStream>().unwrap()
                } else {
                    quote! { #val_str }
                };

                constructor_fields.push(quote! { #ident: #val_tokens });
            } else {
                constructor_args.push(quote! { #ident: impl Into<#ty> });
                constructor_fields.push(quote! { #ident: #ident.into() });
            }
        }
    }

    (constructor_args, constructor_fields)
}

fn extract_field_entries(data: &Data) -> Vec<proc_macro2::TokenStream> {
    let mut arg_entries = Vec::new();

    if let Data::Struct(data_struct) = data
        && let Fields::Named(fields_named) = &data_struct.fields
    {
        for field in &fields_named.named {
            let ident = field.ident.as_ref().unwrap();
            match extract_field_metadata(&field.attrs, ident) {
                FieldMeta::Skip => continue,

                FieldMeta::Simple { tag, section, .. } => {
                    let section_expr =
                        section.as_ref().map_or_else(|| quote! { None }, |s| quote! { Some(#s) });
                    let tag_lit = LitStr::new(&tag, Span::call_site());
                    arg_entries.push(quote! {
                        (#section_expr, #tag_lit, self.#ident.to_string())
                    });
                }

                FieldMeta::Formatted { tag, fmt, section, .. } => {
                    let field_names = extract_field_names(&fmt);
                    let field_idents: Vec<Ident> = field_names
                        .iter()
                        .map(|fname| Ident::new(fname, Span::call_site()))
                        .collect();

                    let format_args = field_idents.iter().map(|id| {
                        quote! { #id = self.#id }
                    });

                    let section_expr =
                        section.as_ref().map_or_else(|| quote! { None }, |s| quote! { Some(#s) });

                    let tag_lit = LitStr::new(&tag, Span::call_site());
                    arg_entries.push(quote! {
                        (#section_expr, #tag_lit, format!(#fmt, #(#format_args),*))
                    });
                }
            }
        }
    }

    arg_entries
}

fn extract_field_metadata(attrs: &[Attribute], ident: &Ident) -> FieldMeta {
    let mut tag_name = ident.to_string();
    let mut fmt_str = None;
    let mut section = None;
    let mut default_value = None;
    let mut saw_xml = false;

    for attr in attrs {
        if !attr.path().is_ident("xml") {
            continue;
        }
        saw_xml = true;

        let _ = attr.parse_nested_meta(|meta| {
            let ident = meta.path.get_ident().map(|i| i.to_string());

            if let Some(name) = ident {
                let value: Option<String> =
                    meta.value().ok().and_then(|v| v.parse::<LitStr>().ok()).map(|lit| lit.value());

                match name.as_str() {
                    "tag" => {
                        if let Some(v) = value {
                            tag_name = v
                        }
                    }
                    "fmt" => {
                        if let Some(v) = value {
                            fmt_str = Some(v)
                        }
                    }
                    "custom_arg" => {
                        section = value;
                    }
                    "value" => {
                        default_value = value;
                    }
                    _ => {}
                }
            }

            Ok(())
        });
    }

    if !saw_xml {
        return FieldMeta::Skip;
    }

    if let Some(fmt) = fmt_str {
        FieldMeta::Formatted { tag: tag_name, fmt, section, default_value }
    } else {
        FieldMeta::Simple { tag: tag_name, section, default_value }
    }
}

/// Extract field names from a format string
/// e.g., "MEM://0x{host_offset:x}:0x{length:x}" -> ["host_offset", "length"]
fn extract_field_names(fmt: &str) -> Vec<String> {
    let re = Regex::new(r"\{([a-zA-Z_][a-zA-Z0-9_]*)(?::[^}]*)?\}").unwrap();
    re.captures_iter(fmt).map(|cap| cap[1].to_string()).collect()
}

/// Convert CamelCase to UPPER-KEBAB-CASE
/// e.g., "BootTo" -> "BOOT-TO"
fn to_cmd_name(ident: &Ident) -> String {
    let s = ident.to_string();
    let mut cmd_name = String::new();

    for (i, c) in s.chars().enumerate() {
        if c.is_uppercase() && i != 0 {
            cmd_name.push('-');
        }
        cmd_name.push(c.to_ascii_uppercase());
    }

    cmd_name
}
