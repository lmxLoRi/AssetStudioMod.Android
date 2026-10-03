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
| Mesh → OBJ | working (`AssetStudioUtility/MeshExtensions.cs`) |
| Font → .ttf/.otf | working |
| VideoClip / MovieTexture → .mp4 | working |
| Texture2D → PNG (via NDK decoder + ImageSharp) | working |
| Sprite → PNG | working, not covered by the synthetic fixture |
| Shader → decompiled text | not ported (the converter lives in the CLI) |
| Animator / AnimationClip | not ported |
| AudioClip → .wav/.ogg | impossible (needs FMOD), falls back to raw/JSON |
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

## Choosing a directory

There are two independent paths, because neither one covers everything:

### All-files access (recommended)

Granting `MANAGE_EXTERNAL_STORAGE` lets the app read and write **any real filesystem path**
directly — no copying at all, which matters a lot for multi-gigabyte bundle sets. The user grants
it once via the **Grant all-files access** button, which opens the per-app toggle in Settings.
After that you can type or paste any path (`/sdcard/Download/mygame`) into **Bundle folder** and
press **Load**.

Two limitations worth knowing:

- **It does not reach other apps' app-specific directories.** The platform documentation is
  explicit: `/sdcard/Android/data/<other package>/` stays inaccessible. Use the Shizuku path
  above for those, or copy them out yourself.
- **Google Play does not permit this permission for asset-extraction apps.** It requires a
  Permissions Declaration Form and an approved use case, and extraction is not one. Fine for
  sideloading; the SAF path keeps the app policy-clean if that ever matters.

### Shizuku (the only thing that reaches Android/data)

`/sdcard/Android/data/<game>` is unreachable both ways above, and Shizuku is what fixes it. Its
service runs as uid 2000 (shell), which has `ext_data_rw` and therefore can read those paths.
The app declares Shizuku's provider and, when a path cannot be read directly, falls back to
staging it through a shell-side `cp -r` into its own external directory, then loads it normally.

Shizuku is still not a privilege escalation of our own process, so this is a copy -- but it is
fully automatic, with no folder picking, which is the part that matters for multi-gigabyte sets.

Four things that are easy to get wrong, all found by running it on a device:

1. **The provider must be declared by the app.** Shizuku's `provider` AAR only contributes a
   permission and a meta-data tag.
2. **`android:permission` must be `android.permission.INTERACT_ACROSS_USERS_FULL`**, not
   Shizuku's `moe.shizuku.manager.permission.API_V23`. The service runs as shell in ADB mode and
   cannot hold a signature permission, so with API_V23 every hand-off is refused
   (`Permission Denial ... requires API_V23`) and `PingBinder()` stays false forever.
3. **`android:multiprocess` must be `false`** or `ShizukuProvider.attachInfo` throws
   `IllegalStateException` during app start.
4. **`<queries>` must list `moe.shizuku.privileged.api`**, otherwise package visibility hides the
   manager and the app never shows up in Shizuku's authorization list.

Also note the staging target must be the app's *external* directory: the shell cannot write into
`/data/user/0/<pkg>`, which is the app's private sandbox.

Shizuku 13.x made `Shizuku.newProcess()` private, so this app goes through
`IShizukuService.NewProcess` via `ShizukuBinderWrapper` instead. That interface's
`waitForTimeout(long, String)` takes a `TimeUnit` *enum name* -- `"ms"` throws
`IllegalArgumentException: No enum constant java.util.concurrent.TimeUnit.ms`.

### Storage Access Framework (fallback, no permission)

**Pick folder (copies files)** opens `ACTION_OPEN_DOCUMENT_TREE` and copies the tree into the
app's external files dir. Always available, but a copy is unavoidable: AssetStudio needs seekable
real files and a `content://` URI cannot provide them. When all-files access happens to be
granted, the app first tries to map the tree back to a real path and reads it in place instead.

Whichever route you take, the folder is **loaded immediately** — there is no separate scan step
(**Rescan** is only there if the folder changed underneath you).

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
adb shell am start -n $ACT -e action load   -e path /sdcard/Download/mygame
adb shell am start -n $ACT -e action export -e kind Texture2D --ez overwrite true

# one-shot: point at a directory, load it and export in a single invocation
adb shell am start -n $ACT -e action export -e path /sdcard/Download/mygame \
    -e kind Texture2D --ez overwrite true
```

`kind` is any `ExportKind` name (`Texture`, `Sprite`, `JsonDump`, `TextAsset`, `RawData`).
`export` scans first if nothing is loaded, since each `am start` is a fresh process.

`path` exists because `adb shell input text` cannot reliably type `/` through a CJK IME, so
setting the path through an intent extra is the only dependable way to script a run.

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

## Export kinds

`Auto` is the default and dispatches on each asset's real `ClassIDType`; the other kinds exist to
export one category in isolation.

| Type | Output |
|---|---|
| `Texture2D` | PNG (RGBA, DXT, ASTC, ETC, PVRTC … via `libTexture2DDecoderNative.so`) |
| `Sprite` | PNG |
| `Mesh` | `.obj` (vertices, UV0, normals, per-submesh groups) |
| `TextAsset` | raw bytes |
| `Font` | `.ttf` / `.otf` (sniffed from the `OTTO` magic) |
| `VideoClip`, `MovieTexture` | `.mp4` |
| everything else | JSON via `Object.Dump()` / `DumpObject()` |

Nothing is skipped for lack of a checkbox: any type without a dedicated exporter falls back to a
JSON dump, and the run summary lists which types were found and which had no exporter, so gaps
are visible rather than silent:

```
3 asset(s): TextAsset x1 Texture2D x2
matched=3 exported=3 skipped=0 failed=0 [TextAsset=1 Texture2D=2]
```

The `.obj` writer came from `AssetStudioCLI/Exporter.cs`, where it was private to the CLI. It is
now in `AssetStudioUtility` so the CLI, GUI and Android app share one implementation. Porting it
also fixed a latent bug: the original used `AppendFormat` without a culture, so under a locale
like `de-DE` it emitted `v 0,5 0,25` and no obj importer could read the result.

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
