# OpenReo – native Windows viewer for Reolink cameras

OpenReo is a standalone Windows client (C# / .NET 8 / WPF) for live monitoring of Reolink IP cameras
(and other RTSP/ONVIF cameras). Video is decoded with FFmpeg using GPU acceleration
(NVDEC/CUDA, D3D11VA, DXVA2).

> **Disclaimer:** OpenReo is an independent open source project. It is not affiliated with, endorsed
> or sponsored by Reolink. "Reolink" is a trademark of its respective owner and is used here only to
> describe compatibility.

> The user interface is available in **English and German**. The language follows the Windows display
> language automatically (German for German Windows, English otherwise) and can be changed in the settings.

## Features

- **Cameras**: automatic discovery via ONVIF (WS-Discovery), manual setup by IP/port/user/password,
  edit, remove, reorder. Stream paths are detected automatically via ONVIF or the known Reolink paths and
  can be verified with "test streams". NVR channels are supported. The camera name configured in the
  camera is read via the Reolink HTTP API. Credentials can be remembered for further cameras.
- **Quality**: Clear (main stream), Mid (medium stream), Low (sub stream) – per camera and globally.
  New cameras start with **Mid**. The global quality switch overrides all individual settings.
  **There is no automatic quality adaption**: even in a 5×5 multiview the real Clear main stream is
  opened and decoded (visible in the info line of every tile).
- **Multiview**: 1, 2, 4, 9, 16, 25 tiles plus 2×3, 3×4, 1+5, 1+7, 1+12, 1+16 and custom layouts
  (layout editor with merged tiles). Double click maximizes/restores, drag & drop from the sidebar and
  between tiles, empty tiles, saved views.
- **Zoom/pan**: mouse wheel zoom (up to 16×) around the cursor, drag to pan, middle mouse button or
  context menu resets – independently per tile. When zoomed, frames are prepared at a higher resolution
  so the picture stays sharp.
- **Audio**: speaker icon in each tile (or context menu) plays the sound of that camera – one camera at a
  time, volume and mute in the toolbar. The audio track is always received (Reolink AAC, ~32–64 kbit/s) but
  only decoded while listening, so switching the sound on never reconnects the stream. Playback latency is
  kept around 100 ms. The camera must have "record audio" enabled (camera settings → streams).
- **Robustness**: connection failures only affect the camera concerned ("offline" with error message and
  countdown), automatic reconnect with exponential back-off. Streams are reused across layout changes
  (no reconnects).
- **Smooth live picture**: an adaptive de-jitter buffer evens out frames that arrive in bursts (Wi-Fi,
  camera busy uploading). It only grows as far as the measured jitter requires – by default up to 1.5 s
  (settings → off / 0.5–2 s). The current delay is shown in the tooltip of the tile's info line.
- **UI**: dark theme (including dark title bar), sidebar with status per camera, toolbar, status bar
  (active streams, GPU/CPU decoders, bitrate, CPU, RAM), fullscreen (F11), context menus.
- **Camera settings** (Reolink HTTP API, right click → "Kameraeinstellungen …"): device name, status LED,
  image (brightness, contrast …), day/night, IR, exposure, mirror/rotate, spotlight, OSD, stream encoding
  (resolution/fps/bitrate per stream), time/NTP, motion and AI sensitivity, recording with weekly
  schedule, FTP (incl. connection test and upload schedule), e-mail (incl. test), push, PTZ with presets,
  siren, reboot, open web UI.
  Only settings reported by the respective camera are shown (including value ranges); old and new
  firmware (V20 commands, schedule modes timer/motion) are detected. Only changed values are sent.
  Intentionally not included (use the web UI/app): firmware update, users, network/Wi-Fi, AI zones, formatting.
- **Playback of recordings** (NAS / network drive): choose the folder where the cameras store their
  recordings (e.g. FTP uploads to TrueNAS, `\\truenas\cameras`); its sub folders are assigned to the cameras
  automatically by name. Per camera ("Wiedergabe"/"Playback"): list of days, 24 h time line (mouse wheel zoom
  down to 5 min, drag to scroll, click to jump), frame accurate seeking, play/pause, ±10 s, 0.25×–16×, sound,
  seamless continuation into the next file, gaps are skipped, "save file as" / show in Explorer. Start times are
  read from the file names (`…_20261118143512.mp4`, `RecM01_20261118_143512_….mp4`, `2026/11/18/143512.mp4`)
  or the file time stamp; durations come from the file time stamps (no need to open the files), so the time line
  is complete immediately. Fragmented MP4 uploads (Reolink FTP) are opened directly at the requested fragment,
  so opening and seeking over SMB takes well under a second instead of several seconds.
  The JPG snapshots the camera uploads with each clip are shown as event markers on the time line (hover:
  preview picture, click: jump to the event).
- **Languages**: English and German, chosen automatically from the Windows display language or set manually
  (settings → language; applied after a restart). All texts are checked by `tools\check-translations.ps1`.
- **Persistence**: cameras (passwords encrypted with Windows DPAPI), quality per camera and global,
  last layout incl. camera assignment, custom layouts, views, window and application settings.

### Keyboard / mouse

| Input | Action |
|---|---|
| F11 | Toggle fullscreen |
| Esc | Leave maximized camera / exit fullscreen |
| Ctrl+1 … Ctrl+6 | Layout 1 / 2 / 4 / 9 / 16 / 25 |
| Double click on tile | Maximize / restore |
| Mouse wheel over tile | Digital zoom |
| Drag (zoomed) | Pan |
| Drag (not zoomed) | Move camera to another tile (swap) |
| Middle mouse button | Reset zoom |

## Camera requirements

With current Reolink firmware RTSP and ONVIF are often **disabled** by default. Enable them under
*Settings → Network → Advanced → Server settings* (or *Port settings*): RTSP (port 554) and ONVIF (port 8000).
The camera settings feature needs HTTP or HTTPS (port 80/443). Battery/Wi-Fi models without a web
interface are not supported.

## Installation

Download `OpenReo-Setup-<version>.exe` from the [Releases](../../releases) page and run it. The installer
contains the .NET runtime (self-contained) and FFmpeg – nothing else needs to be installed. Install per
user (no administrator rights) or for all users. The configuration is stored in
`%APPDATA%\OpenReo\config.json`, logs in `%APPDATA%\OpenReo\logs` (both are kept on uninstall).

Optional: `OpenReo.exe --config <file>` uses a different configuration file (e.g. portable use).

## Build

Requirements: .NET 8 SDK (x64), internet access for NuGet.

```powershell
.\tools\get-ffmpeg.ps1      # once: FFmpeg 8.1 LGPL shared build into third_party\ffmpeg
dotnet build OpenReo.sln    # debug build
.\build.ps1                 # release: self-contained publish + installer (artifacts\)
.\build.ps1 -SkipInstaller  # publish only (artifacts\publish)
```

The Inno Setup compiler is restored from NuGet (`installer\InstallerTools.proj`); no Inno Setup
installation is required.

## Architecture

```
src/
  OpenReo.Core     (no UI, no video dependency)
    Models/                 CameraConfig, LayoutDefinition, ViewState, SavedView, AppSettings
    Configuration/          ConfigurationManager   – JSON, atomic save, .bak, DPAPI
    Cameras/                CameraManager          – camera list + change events
                            RtspUrlBuilder         – URLs per quality, Reolink paths
    Onvif/                  OnvifDiscovery         – WS-Discovery on all IPv4 interfaces
                            OnvifClient            – SOAP (device/media), WS-UsernameToken + HTTP digest
    Reolink/                ReolinkSession         – Reolink HTTP API (login/token, commands, logout)
    Layouts/                LayoutManager          – built-in + custom layouts, views
  OpenReo.Video    (FFmpeg, no UI)
    FFmpegLoader            – loads the native DLLs, redirects FFmpeg logs
    HardwareAcceleration    – shared GPU devices (CUDA / D3D11VA / DXVA2), format selection
    VideoDecoder            – H.264/H.265, GPU with automatic software fallback
    AudioDecoder            – AAC/G.711/… → 48 kHz stereo PCM (decoded only while audible)
    GpuScaler               – scaling on the GPU (scale_cuda) before download
    FrameConverter          – NV12/YUV → BGRA at display size (swscale), correct color space
    FrameExchange           – triple buffer between decoder thread and UI thread
    RtspStream              – one thread per stream: connect, demux, decode, reconnect
    StreamManager           – RTSP stream manager: streams per (camera, quality), reference counting,
                              delayed close, restart on configuration change
    StreamProbe / CameraStreamDetector – stream test and automatic path detection
    PlaybackSession         – playback of recorded files: exact seeking, speed, separate audio reader for lip sync
    Recordings/             – RecordingIndex (file times from names/folders, cached durations), folder matching
  OpenReo.App      (WPF)
    Controls/MultiViewPanel – multiview manager: layout grid, tile pool, maximize, assignment
    Controls/VideoTile      – video, overlay, zoom/pan, drag & drop, context menu
    Video/RenderLoop        – copies new frames once per display refresh into WriteableBitmaps
    Audio/                  – audio player (NAudio) and controller for the audible camera
    CameraSettings/         – camera settings window (generated forms, schedule editor, PTZ)
    Playback/, Controls/TimelineControl – playback window with zoomable 24 h time line
    Dialogs/                – camera, ONVIF discovery, settings, layout editor, recording folders
tools/
  OpenReo.StreamBench       – load test: N real streams through the same video layer, measures fps/CPU/RAM
  OpenReo.UiTest            – automated tests: camera settings (simulated Reolink API), audio, playback, English UI
  get-ffmpeg.ps1            – downloads the FFmpeg libraries
  check-translations.ps1    – verifies that every UI text has an English translation
  deploy-local.ps1          – updates a local installation with the current build (closes/restarts OpenReo)
installer/                  – Inno Setup script
```

### Video path and performance

1. Every camera/quality has exactly **one** RTSP connection (TCP, video only, minimal buffering), even if
   it is shown in several tiles. Streams that are no longer visible stay connected for 15 s
   (configurable) – layout changes and maximizing cause no reconnect.
2. Decoding on the GPU; all decoders share one GPU device. "Automatic" prefers CUDA/NVDEC (NVIDIA)
   because frames can also be scaled on the GPU there; Intel/AMD use D3D11VA, then DXVA2, then software.
3. Every frame is decoded (the selected stream is never changed); for display it is scaled to the actual
   pixel size of the tile (× zoom) – on NVIDIA already on the GPU – and only then copied to system memory.
   Invisible tiles (e.g. behind a maximized camera) get no conversion but stay connected.
4. A triple buffer hands the newest frame to the UI thread; one `WriteableBitmap` per stream is shared by
   all tiles showing that stream.

## Test status

Tested on Windows 11, RTX 5090, 32 logical cores, with FFmpeg generated H.264/H.265 test videos (local
files played in real time) and an unreachable RTSP camera:

| Check | Result |
|---|---|
| 25 × Clear 4K H.265 at the same time (5×5) | 25 fps per stream, all on the GPU, CPU ~2 %, RAM ~3.2 GB |
| 25 × 1440p H.264 | 25 fps, CPU ~1 %, RAM ~1.6 GB |
| 25 × 360p (sub stream) | 25 fps, CPU ~1 %, RAM ~0.5 GB |
| Same with software decoding (comparison) | 25 fps, CPU ~37 % |
| Multiview with Clear stays Clear | yes – all tiles show 3840×2160 |
| Global switch Clear → Mid → Clear | all cameras switch, old streams closed immediately, selection saved |
| Layout changes 25 → 4 → 1 → 25 → 2 → 25 | no reconnect at all |
| Unreachable camera | "offline" with message, retry with back-off, other cameras unaffected |
| Exit / restart | configuration, layout, assignment and quality are restored |

Camera settings: `tools\OpenReo.UiTest` opens the settings window against a simulated Reolink API (old
and V20 firmware formats including responses captured from a real camera, unsupported commands, wrong
password) and verifies among other things that only changed fields are sent; it also checks the audio chain (decode,
playback, latency, switching off) with a test video containing an AAC track, and renders all settings pages in
English to make sure no German text is left. Recording playback is tested with a simulated NAS folder (two naming
schemes, date folders, gaps): time parsing, exact seeking, seamless continuation, gap handling and even audio
delivery for finely interleaved and block-wise stored audio (69 checks, all passing).

**Not tested yet** (no hardware available in the development environment): real Reolink cameras
(including writing camera settings), ONVIF discovery and profile queries against real devices, connection
loss and reconnect of a running network camera, Intel/AMD GPUs (the D3D11VA path was tested on NVIDIA).
Zoom/pan, drag & drop and maximizing by double click are implemented but have not been clicked through
interactively yet.

Load test with real cameras:

```powershell
tools\OpenReo.StreamBench\bin\Release\net8.0-windows\OpenReo.StreamBench.exe `
    --url "rtsp://admin:PASSWORD@192.168.1.10:554/h264Preview_01_main" --count 25 --seconds 30
```

## Known limitations

- The FFmpeg filters `scale_d3d11`/`scale_d3d12` (FFmpeg 8.1) cannot allocate output textures for
  decoder surfaces. With D3D11VA, scaling is therefore done on the CPU (more PCIe transfer); with
  25 × 4K about 21 instead of 25 fps were measured. On NVIDIA, CUDA is preferred for that reason.
- Recording and playback inside OpenReo are not part of this version.

## License

OpenReo is released under the [MIT License](LICENSE). Third-party components (FFmpeg under LGPL 2.1+,
FFmpeg.AutoGen under MIT, …) are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
