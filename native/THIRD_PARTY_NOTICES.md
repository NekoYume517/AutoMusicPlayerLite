# Native WinUI 3 release — third-party notices

Auto Music Player Lite 2.2.0 uses the original project's MIT-licensed core.
Native interface and modifications: Copyright (c) 2026 NekoYume517.
The original core copyright is retained in `LICENSE`. Native source and build instructions
are provided with this release. Library copyright and full license notices are
retained under `licenses/` in the installation folder.

| Component | Version | License / terms | Purpose |
| --- | --- | --- | --- |
| CPython | 3.11.0 | Python Software Foundation license and bundled notices | Offline Python engine |
| pynput | 1.8.2 | GNU LGPL v3 | Keyboard/mouse listeners |
| six | 1.17.0 | MIT | pynput dependency |
| pypinyin | 0.55.0 | MIT | Chinese full-pinyin/initial search index |
| mido | 1.3.3 | MIT | MIDI file import/export |
| packaging | 26.3 | Apache 2.0 / BSD-2-Clause | mido dependency |
| PyYAML | 6.0.3 | MIT; libyaml MIT | Instrument profile/configuration |
| cffi | 2.1.1 | MIT; included libffi notices | Native Python integration |
| pycparser | 3.0 | BSD-3-Clause | cffi dependency |
| PyInstaller bootloader | 6.20.0 | GPL with bootloader exception | Frozen executable distribution |
| .NET runtime | 8.0.25 | MIT; bundled third-party notices | Native frontend runtime |
| Windows App SDK / WinUI | 1.8.260921001 package | Microsoft Windows App SDK terms; bundled third-party notices | Native Windows interface |
| Visual C++ app-local redistributable | 14.51.36231 x64 | Microsoft Visual Studio redistributable terms | C++ runtime |

The Windows App SDK self-contained deployment includes its native runtime
dependencies (including DirectML/ONNX-related binaries) even though this
application does not use AI features. Their original package notices are retained.
No Qt, Audiveris, Jianpu OMR worker, or external Node runtime is shipped in this
native installer. The older root `THIRD_PARTY_NOTICES.md` describes optional
components of the legacy source and is separate from this release inventory.

`pynput` is unmodified and its editable Python source is placed at
`backend/_internal/pynput/`. Its original source is also in
`licenses/pynput-1.8.2-source/`. You may inspect, replace or modify those modules
under their license, including for debugging your modifications. The frozen
engine loads them from this directory rather than embedding them in its PYZ.
The application has no restriction on reverse engineering for debugging changes
to this LGPL dependency. Corresponding application source and build scripts are
included in the accompanying source ZIP.

Upstream sources: https://github.com/xiaominaimoyu/auto_music_player,
https://github.com/moses-palmer/pynput,
https://github.com/mozillazg/python-pinyin,
https://github.com/yaml/pyyaml,
https://github.com/pyca/cryptography,
https://github.com/pyinstaller/pyinstaller,
https://github.com/dotnet/runtime,
https://github.com/microsoft/WindowsAppSDK.
