[简体中文](./README.md)

# <img src="./src/iSay.Desktop/Assets/iSay.png" width="32" alt="" /> iSay

Turn your thoughts into text, with your voice.

iSay is a Windows voice input tool. Press a shortcut to start speaking, see text appear in supported input fields, and press it again to finish. It includes text formatting, a personal vocabulary, a floating recording bar, and Chinese and English interfaces.

[Website](https://isay-voice.pages.dev/?lang=en) · [Source](https://github.com/Diorser/iSay) · [Issues](https://github.com/Diorser/iSay/issues) · [Build guide (Chinese)](./docs/公开/使用与构建.md) · [Privacy and dependencies (Chinese)](./docs/公开/隐私与第三方依赖.md)

[![Source build](https://github.com/Diorser/iSay/actions/workflows/%E6%BA%90%E7%A0%81%E6%9E%84%E5%BB%BA.yml/badge.svg)](https://github.com/Diorser/iSay/actions) [![License: MIT](https://img.shields.io/badge/License-MIT-teal.svg)](./LICENSE)

> **This is the 0.10.1 source preview.** The repository publishes the project's own desktop client, service adapters, TSF input component, and installation and update tools. Third-party native binaries used by the built-in speech, formatting, personalization, and dictionary adapters are excluded. The source can compile without them, but a fresh clone does not provide every feature. No complete public installer is provided until redistribution rights are confirmed. GitHub's automatic Source code archives are not installers.

## Features

| Feature | Details |
|---|---|
| Voice input | Microphone selection, recording shortcuts, recognition status, and a floating recording bar. The built-in adapter requires separately obtained, legitimately usable third-party components and service access. |
| Input integration | A custom Windows TSF component provides text composition; a plain-text compatibility mode is available. Application support must be verified individually. |
| Text formatting | Basic processing, punctuation and spacing preferences, a built-in formatting adapter, and configurable compatible AI endpoints. |
| Personal vocabulary | Vocabulary management, corrections, and optional learning; some functions require third-party native components. Additional online learning is off by default. |
| Local records | Transcription history and optional audio files with retention settings. Custom service keys are protected with the current Windows user's DPAPI. |
| Daily use | Tray controls, sign-in startup, and muting system audio during recording. The final settings tab contains About, language selection, and update controls. |
| Installation and updates | Installer scripts, a separate updater, RSA-signed manifest verification, resumable downloads, transactional file rollback, and interrupted-update recovery. Production update sources and public keys are not configured yet. |
| Languages | Simplified Chinese and English interfaces. Interface language does not determine which languages a speech service recognizes. |

## Platform and defaults

The current implementation uses **C# / WPF / .NET 10** and targets **Windows x64**. The installer requires Windows 10 version 2004 (build 19041) or later. The TSF component is built for both x64 and x86 input hosts. macOS, Linux, and Windows ARM64 are outside this release scope.

New configurations use **RightAlt** in toggle mode: press once to start and again to finish. Sign-in startup and muting system audio during recording are enabled by default. Existing explicit settings are preserved. Transcription history is retained for 30 days by default; audio file saving is off.

The WPF client can use a framework-dependent deployment. Running it requires the **.NET 10 Windows Desktop Runtime x64**. Native components may also require **Visual C++ v14 x64 / x86**. The complete offline installation design bundles Microsoft's original runtime installers and installs them only when needed, so its size includes more than the application itself. The x86 runtime serves native components of that architecture.

## Build from source

Use Windows and the .NET 10 SDK. Native TSF builds also require CMake 3.24+, the Visual Studio 2022 C++ toolchain, and the Windows SDK.

```powershell
git clone https://github.com/Diorser/iSay.git
cd iSay
dotnet build src/iSay.Desktop/iSay.Desktop.csproj -c Release
dotnet build src/iSay.Updater/iSay.Updater.csproj -c Release
dotnet build src/iSay.ReleaseTool/iSay.ReleaseTool.csproj -c Release
```

These commands build the managed source. They do not register the input component or download third-party speech binaries. See the [build guide (Chinese)](./docs/公开/使用与构建.md) for native builds, compatible endpoints, and release prerequisites.

## Structure

| Directory | Purpose |
|---|---|
| `src/iSay.Desktop` | WPF interface, recording, text delivery, settings, and tray. |
| `src/iSay.Voice` | Speech, formatting, vocabulary, personalization, and local data adapters. |
| `src/iSay.TextService` | The project's C++ TSF component, currently version 0.9.7. |
| `src/iSay.TextService.Setup` | Input component registration and maintenance utility. |
| `src/iSay.Updates`, `src/iSay.Updater` | Update verification, downloads, and separate transactional updater. |
| `src/iSay.ReleaseTool`, `scripts/发布` | Release manifests, packaging, and a Cloudflare update service template. |
| `test` | Standalone regression check source; personal test data is excluded. |
| `website` | Static website source. |

## Status

This is an evolving source preview. Isolated source builds and regression checks for update security and file transactions have passed. **Clean installation and real microphone recognition in the Windows 10 VM still need acceptance testing.** An isolated 0.10.0 instance completed 100 start/stop cycles, 30 minutes of fixed-audio recognition and eight hours idle, meeting the measured idle CPU and memory budgets. This excludes real microphones, in-host TSF, formatting and learning, so full-scenario stability remains unverified. Transaction rollback does not include automatic rollback based on post-launch application health checks.

When opening an [issue](https://github.com/Diorser/iSay/issues), include the Windows version, iSay version, target application, and reproduction steps. Remove audio, transcription content, vocabulary, accounts, keys, and personal paths from attached diagnostics.

## License and author

The project's own code is licensed under the [MIT License](./LICENSE), by [Diorser](https://github.com/Diorser). Third-party software, runtimes, fonts, trademarks, and services retain their respective licenses and terms. MIT does not grant redistribution rights for those components or imply third-party authorization or endorsement. See the [dependency notice](./scripts/发布/第三方依赖声明.txt) and [privacy and dependency guide (Chinese)](./docs/公开/隐私与第三方依赖.md).
