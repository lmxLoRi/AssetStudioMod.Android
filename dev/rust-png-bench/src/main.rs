// Measures PNG encoding, and nothing else, so the only variable is the encoder.
//
//   dump  <png...>   decode each PNG to <png>.rgba + <png>.dims, for the C harness to read
//   bench <png...>   time encoding it back, RGBA and RGB, with fast settings
//
// The RGB run exists because 92% of the textures in the test cache are ASTC_RGB_*, which carry no
// alpha channel, and the export writes them as RGBA anyway. A quarter less data into the deflate
// should be a quarter less time, and this is where that gets checked before anything is built on it.

use std::fs;
use std::time::Instant;

use png::{BitDepth, ColorType, Compression, Encoder, FilterType};

struct Sample {
    width: u32,
    height: u32,
    rgba: Vec<u8>,
}

/// The same pixels with the alpha byte dropped, built once outside the timer.
fn to_rgb(rgba: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(rgba.len() / 4 * 3);
    for px in rgba.chunks_exact(4) {
        out.push(px[0]);
        out.push(px[1]);
        out.push(px[2]);
    }
    out
}

fn decode(path: &str) -> Sample {
    let data = fs::read(path).expect("read");
    let mut decoder = png::Decoder::new(&data[..]);
    decoder.set_transformations(png::Transformations::normalize_to_color8());
    let mut reader = decoder.read_info().expect("header");
    let mut buf = vec![0u8; reader.output_buffer_size()];
    let info = reader.next_frame(&mut buf).expect("frame");
    buf.truncate(info.buffer_size());
    Sample { width: info.width, height: info.height, rgba: buf }
}

fn encode(s: &Sample, color: ColorType, data: &[u8]) -> usize {
    let mut out = Vec::new();
    {
        let mut enc = Encoder::new(&mut out, s.width, s.height);
        enc.set_color(color);
        enc.set_depth(BitDepth::Eight);
        enc.set_compression(Compression::Fast);
        enc.set_filter(FilterType::Paeth);
        let mut w = enc.write_header().expect("header");
        w.write_image_data(data).expect("data");
        w.finish().expect("finish");
    }
    out.len()
}

fn time<F: FnMut() -> usize>(iters: usize, mut f: F) -> f64 {
    f();
    f(); // warm the code paths the same way in every configuration
    let t = Instant::now();
    for _ in 0..iters {
        f();
    }
    t.elapsed().as_secs_f64() * 1000.0 / iters as f64
}

fn short(path: &str) -> &str {
    path.rsplit('/').next().unwrap_or(path)
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 2 {
        eprintln!("usage: rust-png-bench dump|bench <png...>");
        return;
    }

    match args[0].as_str() {
        "dump" => {
            for path in &args[1..] {
                let s = decode(path);
                fs::write(format!("{path}.rgba"), &s.rgba).expect("write rgba");
                fs::write(format!("{path}.dims"), format!("{} {}", s.width, s.height)).expect("write dims");
                println!("{path}: {}x{} -> {}.rgba", s.width, s.height, path);
            }
        }
        "bench" => {
            let backend = if cfg!(feature = "czlib") { "C zlib" } else { "fdeflate via Compression::Fast" };
            println!("deflate backend: {backend}");
            for path in &args[1..] {
                let s = decode(path);
                let rgb = to_rgb(&s.rgba);
                let iters = if (s.width as u64 * s.height as u64) > 2_000_000 { 3 } else { 10 };

                let mut rgba_size = 0usize;
                let rgba = time(iters, || {
                    rgba_size = encode(&s, ColorType::Rgba, &s.rgba);
                    rgba_size
                });

                let mut rgb_size = 0usize;
                let rgb_ms = time(iters, || {
                    rgb_size = encode(&s, ColorType::Rgb, &rgb);
                    rgb_size
                });

                println!(
                    "  {:38} {:>5}x{:<5} RGBA {:7.1} ms {:>6} KB   RGB {:7.1} ms {:>6} KB   ({:.2}x)",
                    short(path), s.width, s.height,
                    rgba, rgba_size / 1024,
                    rgb_ms, rgb_size / 1024,
                    rgba / rgb_ms
                );
            }
        }
        other => eprintln!("unknown mode {other}"),
    }
}
