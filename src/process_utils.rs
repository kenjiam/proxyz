use std::process::Command;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub fn hidden_command(program: &str) -> Command {
    let mut command = Command::new(program);
    configure_hidden(&mut command);
    command
}

pub fn configure_hidden(command: &mut Command) {
    #[cfg(windows)]
    {
        command.creation_flags(CREATE_NO_WINDOW);
    }
}

pub fn decode_output_text(bytes: &[u8]) -> String {
    if bytes.is_empty() {
        return String::new();
    }

    if let Ok(text) = std::str::from_utf8(bytes) {
        return text.to_string();
    }

    if let Some(text) = decode_utf16_output(bytes) {
        return text;
    }

    #[cfg(windows)]
    {
        if let Some(text) = decode_windows_multibyte(bytes) {
            return text;
        }
    }

    String::from_utf8_lossy(bytes).to_string()
}

fn decode_utf16_output(bytes: &[u8]) -> Option<String> {
    if bytes.len() < 2 || bytes.len() % 2 != 0 {
        return None;
    }

    let looks_like_utf16 = bytes.starts_with(&[0xFF, 0xFE])
        || bytes.starts_with(&[0xFE, 0xFF])
        || bytes
            .iter()
            .skip(1)
            .step_by(2)
            .filter(|byte| **byte == 0)
            .count()
            * 4
            >= bytes.len();

    if !looks_like_utf16 {
        return None;
    }

    let little_endian = !bytes.starts_with(&[0xFE, 0xFF]);
    let mut units = bytes
        .chunks_exact(2)
        .map(|chunk| {
            if little_endian {
                u16::from_le_bytes([chunk[0], chunk[1]])
            } else {
                u16::from_be_bytes([chunk[0], chunk[1]])
            }
        })
        .collect::<Vec<_>>();

    if matches!(units.first(), Some(0xFEFF)) {
        units.remove(0);
    }

    String::from_utf16(&units).ok()
}

#[cfg(windows)]
fn decode_windows_multibyte(bytes: &[u8]) -> Option<String> {
    let oem = decode_with_code_page(bytes, 1)?;
    if contains_meaningful_text(&oem) {
        return Some(oem);
    }

    decode_with_code_page(bytes, 0)
}

#[cfg(windows)]
fn contains_meaningful_text(text: &str) -> bool {
    text.chars()
        .any(|ch| !matches!(ch, '\u{0}'..='\u{1F}' | '\u{7F}') || matches!(ch, '\n' | '\r' | '\t'))
}

#[cfg(windows)]
fn decode_with_code_page(bytes: &[u8], code_page: u32) -> Option<String> {
    unsafe extern "system" {
        fn MultiByteToWideChar(
            code_page: u32,
            dw_flags: u32,
            lp_multi_byte_str: *const u8,
            cb_multi_byte: i32,
            lp_wide_char_str: *mut u16,
            cch_wide_char: i32,
        ) -> i32;
    }

    let wide_len = unsafe {
        MultiByteToWideChar(
            code_page,
            0,
            bytes.as_ptr(),
            bytes.len().try_into().ok()?,
            std::ptr::null_mut(),
            0,
        )
    };
    if wide_len <= 0 {
        return None;
    }

    let mut wide = vec![0u16; wide_len as usize];
    let converted = unsafe {
        MultiByteToWideChar(
            code_page,
            0,
            bytes.as_ptr(),
            bytes.len().try_into().ok()?,
            wide.as_mut_ptr(),
            wide_len,
        )
    };
    if converted <= 0 {
        return None;
    }

    String::from_utf16(&wide).ok()
}
