// Measures PNG encoding, and nothing else, so the only variable is the encoder.
//
//   dump  <png...>   decode each PNG to <png>.rgba + <png>.dims, for the C harness to read
//   bench <png...>   time encoding it back, with fast settings matching the shipping encoder
//
// Run once plain and once with --features czlib. The difference between those runs is the deflate
// implementation (pure-Rust miniz_oxide against C zlib); the difference between either and the C
// harness is the language.

use std::fs;
use std::time::Instant;

use png::{BitDepth, ColorType, Compression, Encoder, FilterType};

struct Sample {
    width: u32,
    height: u32,
    rgba: Vec<u8>,
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

/// `settings` of None leaves the crate on its own defaults, which is the honest baseline for what
/// Rust would do out of the box. Returns the encoded size in bytes.
fn encode(s: &Sample, settings: Option<(Compression, FilterType)>) -> usize {
    let mut out = Vec::new();
    {
        let mut enc = Encoder::new(&mut out, s.width, s.height);
        enc.set_color(ColorType::Rgba);
        enc.set_depth(BitDepth::Eight);
        if let Some((compression, filter)) = settings {
            enc.set_compression(compression);
            enc.set_filter(filter);
        }
        let mut w = enc.write_header().expect("header");
        w.write_image_data(&s.rgba).expect("data");
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
            let backend = if cfg!(feature = "czlib") { "C zlib" } else { "miniz_oxide (pure Rust)" };
            println!("deflate backend: {backend}");
            for path in &args[1..] {
                let s = decode(path);
                let iters = if (s.width as u64 * s.height as u64) > 2_000_000 { 3 } else { 10 };

                let mut fast_size = 0usize;
                let fast = time(iters, || {
                    fast_size = encode(&s, Some((Compression::Fast, FilterType::Paeth)));
                    fast_size
                });

                let mut def_size = 0usize;
                let def = time(iters, || {
                    def_size = encode(&s, None);
                    def_size
                });

                println!(
                    "  {:38} {:>5}x{:<5} fast(Paeth,L1) {:7.1} ms {:>6} KB   crate default {:7.1} ms {:>6} KB",
                    short(path), s.width, s.height,
                    fast, fast_size / 1024,
                    def, def_size / 1024
                );
            }
        }
        other => eprintln!("unknown mode {other}"),
    }
}
