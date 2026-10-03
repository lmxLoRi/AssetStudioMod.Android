// The Rust PNG encoder, as a C ABI the app can P/Invoke.
//
// The point is the png crate's Compression::Fast, which routes to fdeflate -- a deflate written
// specifically for fast PNG encoding -- rather than to a general-purpose one like zlib level 1.
// On desktop x86_64 that measured 6-7.6x the shipping C zlib encoder on identical pixels; this
// exists to find out whether it holds on the phone.

use std::fs;
use std::os::raw::{c_char, c_int, c_uchar};
use std::slice;

use png::{BitDepth, ColorType, Compression, Encoder, FilterType};

/// Writes `bgra` (tightly packed 8-bit BGRA, top row first, which is what the decoder produces)
/// to `path` as a PNG. Returns 0 on success, negative on failure.
///
/// Takes BGRA rather than RGBA because PNG has no BGRA colour type, so the swap has to happen
/// somewhere: doing it here, where it is a tight loop over a byte slice, is worth several
/// milliseconds per image over doing it per pixel in the managed caller.
///
/// Never unwinds: this is called across FFI.
///
/// # Safety
/// `path` must be a NUL-terminated string and `bgra` must point at width*height*4 readable bytes.
#[no_mangle]
pub unsafe extern "C" fn rust_png_write_bgra(
    path: *const c_char,
    bgra: *const c_uchar,
    width: c_int,
    height: c_int,
    flip_vertical: c_int,
) -> c_int {
    if path.is_null() || bgra.is_null() || width <= 0 || height <= 0 {
        return -1;
    }

    let len = width as usize * height as usize * 4;
    let source = slice::from_raw_parts(bgra, len);

    // BGRA to RGBA and, when asked, bottom-up to top-down, in the same pass. The rows are
    // reversed by writing them to the opposite end of the destination, which is the same bytes
    // moved and costs nothing; the caller flipping the image first was 60.2s of CPU for 8487
    // textures, 26% of everything the texture decoder appeared to cost.
    let stride = width as usize * 4;
    let mut pixels = vec![0u8; len];
    let rows = height as usize;
    for y in 0..rows {
        let dst_row = if flip_vertical != 0 { rows - 1 - y } else { y };
        let src_row = &source[y * stride..(y + 1) * stride];
        let dst_row = &mut pixels[dst_row * stride..(dst_row + 1) * stride];
        for (dst, src) in dst_row.chunks_exact_mut(4).zip(src_row.chunks_exact(4)) {
            dst[0] = src[2];
            dst[1] = src[1];
            dst[2] = src[0];
            dst[3] = src[3];
        }
    }
    let pixels: &[u8] = &pixels;

    let mut out: Vec<u8> = Vec::with_capacity(len / 2 + 1024);
    {
        let mut enc = Encoder::new(&mut out, width as u32, height as u32);
        enc.set_color(ColorType::Rgba);
        enc.set_depth(BitDepth::Eight);
        // Fast is what selects fdeflate. Paeth matches what the C encoder was configured with, so
        // the comparison is like for like on the filter as well as the container.
        enc.set_compression(Compression::Fast);
        enc.set_filter(FilterType::Paeth);

        let mut writer = match enc.write_header() {
            Ok(w) => w,
            Err(_) => return -2,
        };
        if writer.write_image_data(pixels).is_err() {
            return -3;
        }
        if writer.finish().is_err() {
            return -4;
        }
    }

    // Written through std::fs rather than handed a FILE* so the C side does not have to own one.
    let name = match std::ffi::CStr::from_ptr(path).to_str() {
        Ok(s) => s,
        Err(_) => return -5,
    };
    match fs::write(name, &out) {
        Ok(()) => 0,
        Err(_) => -6,
    }
}
