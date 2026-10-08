# Native build

Windows x64: .NET SDK 8, Python 3.11, Visual Studio C++ redistributable files and Inno Setup 6.
Pinned dependencies are in `requirements-build.txt`; WinUI packages are pinned in `WinUI/AutoMusicPlayer.csproj`.

```powershell
.\native\build.ps1 -Python python -VcRuntime "C:\path\to\Microsoft.VC145.CRT" -InnoCompiler "C:\path\to\ISCC.exe" -OutputDirectory "C:\path\to\outputs"
```

This runs Python tests, freezes the local engine, publishes self-contained .NET/WinUI, assembles licenses, runs native UI/file-picker integration checks, then compiles the installer. Build outputs remain in `work/`. No user data is included.

Software updates use only this repository's `/releases/latest` stable release. Release assets must include `AutoMusicPlayerLite-{version}-Setup-x64.exe` and the GitHub-provided `sha256:` digest. Tags must be `vX.Y.Z`. Set all version constants and installer names together when publishing a new release. Updater rejects older/equal versions, pre-releases, foreign download URLs, missing digests, oversized installers, truncated downloads and hash mismatch. Downloads are staged to a partial file and promoted only after successful verification; installation is initiated by the user in Settings.

```powershell
dotnet run --project native/tests/UpdateTests --configuration Release
```

Data is stored in `%LOCALAPPDATA%\AutoMusicPlayerLitePublic`. Initial installations contain no scores. SQLite schema version 3 stores multi-group memberships, favorites, content update timestamps and per-score settings. Migration backs up existing databases first. Uninstall preserves data. Mini player and picker use `WS_EX_NOACTIVATE` / `MA_NOACTIVATE`; search temporarily captures physical keys and ignores injected playback events. Input output uses Win32 SendInput; native tests use a recording driver without real OS input.

Licenses and editable pynput source are assembled by `prepare_release.py`. Installer is unsigned unless signing infrastructure is supplied. The frontend uses administrator-compatible Microsoft.Windows.Storage.Pickers.
