# Third-party components

OpenReo uses the following third-party software.

| Component | Version | License | Use |
|---|---|---|---|
| [FFmpeg](https://ffmpeg.org) (libavcodec, libavformat, libavutil, libswscale, libavfilter, libswresample, libavdevice) | 8.1 – LGPL shared build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) | LGPL 2.1 or later | RTSP client, H.264/H.265 decoding (CPU and GPU), image conversion, GPU scaling |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | 8.1.0 | MIT | C# bindings for the FFmpeg C API |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIT | Audio playback (camera sound) |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) | 8.0.0 | MIT | Encrypting the stored camera passwords (Windows DPAPI) |
| .NET 8 / WPF | 8.0 | MIT | Runtime and UI framework (included self-contained in the installer) |

Build tools only (not shipped):

| Component | Version | License | Use |
|---|---|---|---|
| [Inno Setup](https://jrsoftware.org/isinfo.php) via NuGet package `Tools.InnoSetup` | 6.7.3 | Inno Setup License | Building the installer |
| Microsoft.Build.NoTargets | 3.7.56 | MIT | Helper project for restoring Inno Setup |

## FFmpeg (LGPL)

The FFmpeg libraries are linked dynamically (separate DLLs in the `ffmpeg` sub folder) and may be
replaced by the user with compatible builds of the same major version (avcodec-62 …).
The FFmpeg license text is installed as `ffmpeg\LICENSE-FFmpeg.txt`.
Source code: https://git.ffmpeg.org/ffmpeg.git (tag n8.1) and the build scripts at
https://github.com/BtbN/FFmpeg-Builds.
