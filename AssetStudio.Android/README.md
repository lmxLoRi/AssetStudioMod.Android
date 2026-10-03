# AssetStudio for Android

`AssetStudio.Android` is a headless-capable Android app that reuses the existing AssetStudio
parsing libraries. No part of the bundle/serialized-file parsing was rewritten: the whole
`AssetStudio`, `AssetStudioUtility` and `Texture2DDecoderWrapper` sources compile for
`net10.0-android` unmodified.

## What works today

| Capability | Status |
|---|---|
| Bundle container parsing (UnityFS / CAB / zip / gzip / brotli / web / resources) | working |
| Decompression: LZ4, LZMA, Zstd, Brotli, none | working |
| Serialized file parsing, TypeTree, all asset classes | working |
| Texture2D → PNG (via NDK-built `Texture2DDecoderNative` + ImageSharp) | working |
| Sprite → PNG | working |
| Any asset → JSON (`Object.Dump()` / `DumpObject()`) | working |
| TextAsset → raw bytes, any asset → raw bytes | working |
| Mesh → OBJ | not ported yet (the OBJ writer is private to `AssetStudioCLI/Exporter.cs`) |
| Sprite → PNG | code present, not covered by the synthetic test fixture |
| FBX export | **dropped on Android** (see below) |
| Audio decoding (FMOD) | **dropped on Android** (see below) |
| Oodle-compressed bundles | **not supported** (see below) |

## Building

### 1. Native texture decoder

`Texture2DDecoderNative` has to be cross-compiled with the NDK. The repository's existing
`CMakeLists.txt` works with the NDK toolchain unchanged:

```bash
NDK=/path/to/android-ndk-r27c
cmake -S Texture2DDecoderNative -B build/t2d-arm64 \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 -DCMAKE_BUILD_TYPE=Release
cmake --build build/t2d-arm64 -j"$(nproc)"
$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip --strip-unneeded \
  build/t2d-arm64/libTexture2DDecoderNative.so

# then copy into AssetStudio.Android/jniLibs/<abi>/
```

Do the same for `ANDROID_ABI=x86_64` for the emulator. Prebuilt copies for both ABIs are
already committed under `AssetStudio.Android/jniLibs/`.

The Android app resolves it through `Texture2DDecoderWrapper`, whose `T2DDll.DllName` is
`"Texture2DDecoderNative"` — exactly the `libTexture2DDecoderNative.so` name the NDK build
produces.

### 2. App

```bash
dotnet workload install android
dotnet publish AssetStudio.Android/AssetStudio.Android.csproj -c Release
# -> AssetStudio.Android/bin/Release/net10.0-android/publish/com.aelurum.assetstudiomod-Signed.apk
```

`AssetStudio.Android` is deliberately **not** added to `AssetStudio.sln`: the solution is
restored by the Windows CI with `nuget restore`, which has no `android` workload available.

## Using it

Input and output both live under `getExternalFilesDir(null)`:

```
/sdcard/Android/data/com.aelurum.assetstudiomod/files/bundles   <- input
/sdcard/Android/data/com.aelurum.assetstudiomod/files/out       <- output, one dir per run
```

which means you can sideload bundles with `adb push` / a file manager and pull results the
same way:

```bash
adb push mygame.unity3d /sdcard/Android/data/com.aelurum.assetstudiomod/files/bundles/
adb pull /sdcard/Android/data/com.aelurum.assetstudiomod/files/out/
```

1. **Import bundle folder...** — opens `ACTION_OPEN_DOCUMENT_TREE` and copies the picked tree
   into `files/bundles`. The copy is required, not cosmetic: AssetStudio addresses files by
   real path and uses `Directory.GetFiles(..., SearchOption.AllDirectories)`, which has no
   `content://` equivalent. You can skip this entirely by pushing files into that folder.
2. **Scan / load** — walks the imported directory and loads every Unity file.
3. Pick an export kind and press **Export**.
4. **Run codec self-test** — verifies every decompressor and the native texture decoder
   without needing any input files. This is the fastest way to confirm a build is sane on a
   new device.

### Scripting it from adb

Every action can be triggered through an intent extra, which avoids tapping and makes smoke
tests possible:

```bash
ACT=com.aelurum.assetstudiomod/crc6457c8bc28ddf7d589.MainActivity

adb shell am start -n $ACT -e action selftest
adb shell am start -n $ACT -e action scan
adb shell am start -n $ACT -e action export -e kind Texture2D --ez overwrite true
```

`kind` is any `ExportKind` name (`Texture2D`, `Sprite`, `JsonDump`, `TextAsset`, `RawData`).
`export` scans first if nothing is loaded, since each `am start` is a fresh process.

## Why certain things are missing

### FBX export

Autodesk's FBX SDK has no Android build and no arm64 binaries; `AssetStudioFBXNative` is only
published for linux-x64, osx-x64 and osx-arm64. On Android the target therefore
- drops the `AssetStudioFBXWrapper` project reference, and
- excludes `ModelExporter.cs` and `ModelConverter.cs` from `AssetStudioUtility`.

`ModelConverter` is excluded because its only use of the FBX wrapper is `Fbx.QuaternionToEuler`,
which is itself a `DllImport` into `AssetStudioFBXNative` rather than managed math. Nothing
else in `AssetStudioUtility` references either type, and the desktop targets are unaffected.

### FMOD audio

`AssetStudioUtility/Audio/FMODStudioAPI/fmod.cs` is ~5,200 lines of `DllImport` against
Firelight's proprietary FMOD Studio, which has no redistributable Android build. It still
compiles (and will be trimmed away), but `AudioClipConverter` cannot work on Android.

### Oodle / ooz

`Ooz_Decompress` is a thin dispatcher that is **not** part of upstream `zao/ooz`; the
prebuilt `libooz.*` binaries shipped in `AssetStudioCLI/Libraries/` were built from a source
tree that is not in this repository. Rebuilding it for bionic additionally requires
`zao/ooz`, which is currently unreachable from this network. Bundles using
`CompressionType.Oodle` will throw; everything else is unaffected.

## Upstream source fixes required for ARM

Two genuine portability bugs in `Texture2DDecoderNative` had to be fixed before it would
build for `arm64-v8a`. Both also affect plain clang/gcc builds on Linux.

1. **`crunch/crn_decomp.h`** detected 64-bit pointers with
   `#ifdef _WIN64 / #ifdef __x86_64__`, so on aarch64 it fell through to
   `typedef uint32 ptr_bits` — truncating every pointer passed to `crnd_realloc`. The
   Crunch decompressor had simply never been built for ARM. Now keyed off pointer width.
2. **`etc.cpp`** initialised `const uint_fast8_t code[2] = {data[3] >> 5, ...}`, which is an
   illegal narrowing conversion in a braced initialiser. MSVC accepts it as an extension;
   clang rejects it. Now explicitly cast.

## UI notes (things that bite on modern Android)

The UI is built in code with no layout XML, which surfaced two problems worth knowing about if
you extend it:

- **targetSdk >= 35 is edge-to-edge by default** (Android 15/16), so `SetContentView` lays the
  content out *behind* the status bar and toolbar. Without an `IOnApplyWindowInsetsListener`
  padding by `WindowInsets.Type.SystemBars() | DisplayCutout()`, the first row of buttons sits
  underneath the title bar. See `MainActivity.InsetListener`.
- **Do not nest a ScrollView inside another ScrollView and call `FullScroll` on the inner
  one.** `ScrollView.FullScroll` delegates to the parent first, so scrolling the log to the
  bottom scrolls the whole page and hides the buttons above it. The log instead shows only its
  most recent `LogVisibleLines` lines, which keeps the page short enough that no scrolling is
  needed.
- Long operations run on a background thread, so every view update goes through
  `RunOnUiThread` (`SetStatus`, `Append`, `Report`) or it throws `CalledFromWrongThreadException`.

## Known issues / next steps

- **`BigArrayPool<byte>` requests 256 MB × 5 buckets** (`AssetStudio/BigArrayPool.cs`). This
  is hostile on low-end devices and should be made size- or device-dependent.
- **No cancellation.** Nothing in AssetStudio takes a `CancellationToken` and every code path
  is synchronous, so a large extraction cannot be interrupted. The app runs it on a background
  thread to avoid ANR, but cannot cancel.
- **`TrimMode` is `partial`.** `System.Text.Json` reflection (`FloatConverter`,
  `ImportOptions`) and `MakeGenericType` (`PPtrConverter`, `KVPConverter`) are reachable only
  reflectively and are silently removed under `TrimMode=full` (the `IL2026` warnings). Full
  trimming needs a source-generated `JsonSerializerContext` plus `DynamicallyAccessedMembers`
  annotations.
- **Mesh/OBJ and animation export** are not wired up; the implementations live in
  `AssetStudioCLI/Exporter.cs` as private methods and would need porting into
  `AssetStudioUtility`.
- **`Object.Name` is never assigned by the library.** It is a public field that nothing writes;
  the real asset name lives in `NamedObject.m_Name`. Use `NamedObject.m_Name`, and append the
  pathID (`Extractor.DisplayName`) because names are only unique per container, not globally.
- `Studio_temp` for on-disk bundle decompression defaults to
  `Directory.GetCurrentDirectory()` (`AssetStudio/BundleFile.cs`), which is `/` on Android and
  not writable. Avoid `--decompress-to-disk`-style flows or redirect that path to
  `cacheDir`.

## Verified end to end

`SelfTest` covers the platform-sensitive pieces (every decompressor, the NDK texture decoder,
reflection-based JSON, ImageSharp) without needing input files.

The full pipeline was additionally verified on a physical device (Android 16, arm64-v8a) using
a synthetic UnityFS bundle generated for the purpose — a real `UnityFS` v6 container with
LZ4HC blocks wrapping a real `SerializedFile` (format 17) holding a 4x4 RGBA32 `Texture2D`,
an 8x8 DXT1 `Texture2D` and a `TextAsset`. Results:

```
self-test                       14 passed, 0 failed
scan                            Loaded 1 serialized file(s), 3 object(s)
export Texture2D                matched=2 exported=2 skipped=0 failed=0
export JsonDump                 matched=3 exported=3 skipped=0 failed=0
export TextAsset                matched=1 exported=1 skipped=0 failed=0
```

Both PNGs decode to a single colour, `RGBA(255,0,0,255)` — the DXT1 one only gets there via
`libTexture2DDecoderNative.so`, so that also confirms the NDK build and jniLibs packaging.
