# Compresser

A tiny (~20 KB) right-click compressor and extractor for Windows 10 (1803+) and 11. It has no dependencies.

It uses **libarchive**, which Windows already ships as `System32\archiveint.dll` (Explorer and `tar.exe` use the same library). That means the exe only contains the UI and glue code.

## Download

Get `Compresser.exe` from the [latest release](https://github.com/rA9-001/Skidbladnir/releases/latest), double-click it, and choose **Yes** to install.

The exe isn't code-signed, so Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**.

## Features

| Right-click on | Menu entry | Result |
|---|---|---|
| Any file or folder (multi-select works) | **Compress to ZIP** | `name.zip` next to it. Several selected items are merged into one zip. |
| zip, rar, 7z, tar, gz/tgz, bz2, xz, zst, lzma, cab, iso, cpio, xar, lzh, jar, apk | **Extract here** | Extracts into the current folder. Asks before overwriting files. |
| (same) | **Extract to folder** | Extracts into a new folder `name\`. |

- ZIPs use **Deflate level 9**, the smallest zip that every tool, including Windows Explorer, can open.
- Unicode file names are fully supported.
- A progress window with a Cancel button appears for anything that takes longer than about 0.4 s.
- Asks for a password for encrypted zip and 7z archives. Encrypted RAR is not supported by libarchive.
- Blocks malicious archive paths (`..`, absolute paths, writing through symlinks).

## Build

```
build.cmd
```

This uses the C# compiler that ships with Windows (.NET Framework 4.x). No SDK is needed.

## Install / uninstall

Double-click `Compresser.exe` and choose **Yes** (install) or **No** (uninstall).
Or run `Compresser.exe /install` or `Compresser.exe /uninstall`.

Installation is per-user (HKCU) and needs no admin rights. The exe is copied to `%LOCALAPPDATA%\Programs\Compresser\`.

On Windows 11 the entries appear under **Show more options** (or Shift+Right-click). That's the classic menu. Putting them in the new top-level menu would require a signed, packaged shell extension.
