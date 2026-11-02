[English](./README.en.md)

# <img src="./src/iSay.Desktop/Assets/iSay.png" width="32" alt="" /> iSay

让想法，顺着声音变成文字。

iSay 是一款面向 Windows 的语音输入工具：按下快捷键开始说话，在支持的输入框中实时呈现文字，再按一次完成输入。支持智能整理、个人词典、悬浮录音条和中英文界面。

[官网](https://isay-voice.pages.dev/) · [查看源码](https://github.com/Diorser/iSay) · [反馈问题](https://github.com/Diorser/iSay/issues) · [使用与构建](./docs/公开/使用与构建.md) · [完整安装包说明](./docs/公开/完整安装包发布说明.md) · [隐私与第三方依赖](./docs/公开/隐私与第三方依赖.md)

[![源码构建](https://github.com/Diorser/iSay/actions/workflows/%E6%BA%90%E7%A0%81%E6%9E%84%E5%BB%BA.yml/badge.svg)](https://github.com/Diorser/iSay/actions) [![License: MIT](https://img.shields.io/badge/License-MIT-teal.svg)](./LICENSE)

> **当前提供 0.10.1 完整安装预览版和自有源码。** 完整包包含现有识别、整理、学习、词典组件，以及按需安装的 .NET Desktop / VC++ 环境。第三方原生组件不属于本项目 MIT 范围，单独再分发授权尚未取得；详情见[完整安装包说明](./docs/公开/完整安装包发布说明.md)。Windows 10 新机安装与真实麦克风识别仍待验收，请先试用预览版。

## 下载

[下载 Windows x64 完整安装包 · 192.17 MiB](https://github.com/Diorser/iSay/releases/download/v0.10.1-installer-preview/iSay-0.10.1-win-x64-full.exe) · [发行说明与 SHA256](https://github.com/Diorser/iSay/releases/tag/v0.10.1-installer-preview)

也可从[官网](https://isay-voice.pages.dev/)进入下载。无需预先另找 .NET 或 VC++ 安装器；安装向导会检测并按需安装随包环境。当前安装器未签 Windows 发布者签名，客户端自动更新服务尚未启用。GitHub 的 Source code 压缩包是源码，不是安装器。

## 功能

| 功能 | 说明 |
|---|---|
| 语音输入 | 麦克风选择、快捷键启停、实时识别状态和悬浮录音条。完整安装包已含现有第三方组件；源码构建需另备组件，远端服务仍需可用。 |
| 输入框集成 | 自有 Windows TSF 组件提供组合文本输入；可切换普通文本兼容模式。不同应用的输入框支持情况需要分别验证。 |
| 智能整理 | 基础文本处理、标点与间距偏好、内置整理适配器，以及可配置的兼容 AI 接口。 |
| 个人词典 | 词条管理、纠错与可选学习；部分能力依赖第三方原生组件。额外联网学习默认关闭。 |
| 本机记录 | 转写历史与可选录音保存，支持保留期限配置；自定义服务密钥使用当前 Windows 用户的 DPAPI 保护。 |
| 日常使用 | 托盘、登录自启、录音时静音；设置最后一页提供“关于”、语言选择与更新入口。 |
| 安装与更新 | Windows 安装脚本、独立更新器、RSA 签名清单验证、断点续传、文件事务回滚与中断恢复。正式更新源与客户端公钥尚未配置。 |
| 中英文 | 简体中文、英文界面。界面语言不代表语音服务支持相同的识别语言。 |

## 平台与默认设置

当前主线使用 **C# / WPF / .NET 10**，目标为 **Windows x64**；安装器门槛为 Windows 10 版本 2004（19041）及以上。TSF 组件同时构建 x64 与 x86，以适配两种位数的输入宿主。macOS、Linux 和 Windows ARM64 暂不在本次发行范围。

新配置默认使用 **右 Alt（RightAlt）**，按一次开始、再按一次结束；默认勾选登录自启和录音时静音。已有明确配置不会被这些新默认值覆盖。转写历史默认保留 30 天，录音文件默认不保存。

WPF 可以采用依赖系统运行时的发布方式。运行桌面程序需要 **.NET 10 Windows Desktop Runtime x64**；原生组件还可能需要 **Visual C++ v14 x64 / x86**。完整离线安装方案会携带微软官方运行时安装器，检测后按需安装，因此包体积不能只按自有程序计算；x86 运行库用于对应位数的原生组件，与 x64 运行库用途不同。

## 从源码构建

准备 Windows、.NET 10 SDK；编译 TSF 组件另需 CMake 3.24+、Visual Studio 2022 C++ 工具链与 Windows SDK。

```powershell
git clone https://github.com/Diorser/iSay.git
cd iSay
dotnet build src/iSay.Desktop/iSay.Desktop.csproj -c Release
dotnet build src/iSay.Updater/iSay.Updater.csproj -c Release
dotnet build src/iSay.ReleaseTool/iSay.ReleaseTool.csproj -c Release
```

以上命令构建托管源码，不会安装输入组件，也不会下载第三方识别引擎。TSF 双架构构建、兼容接口配置和发行前提见[使用与构建](./docs/公开/使用与构建.md)。

## 项目结构

| 目录 | 用途 |
|---|---|
| `src/iSay.Desktop` | WPF 界面、录音、输入交付、设置与托盘。 |
| `src/iSay.Voice` | 语音服务、整理、词典、个性化与本机数据适配。 |
| `src/iSay.TextService` | 自有 C++ TSF 输入组件，当前组件版本 0.9.7。 |
| `src/iSay.TextService.Setup` | 输入组件注册与维护程序。 |
| `src/iSay.Updates`、`src/iSay.Updater` | 更新校验、下载和独立文件事务更新。 |
| `src/iSay.ReleaseTool`、`scripts/发布` | 发行清单、打包和 Cloudflare 更新服务模板。 |
| `test` | 可独立运行的回归检查源码；不含个人测试数据。 |
| `website` | 静态官网源码。 |

## 当前状态

本次公开的是持续完善中的源码预览。独立源码编译、更新安全及文件事务等回归已完成；隔离的 0.10.0 实例已完成 100 次启停、30 分钟固定音频识别和 8 小时空闲测试，空闲 CPU 与内存符合本次预算。该测试不包含真实麦克风、输入宿主中的 TSF、智能整理或学习，**Windows 10 虚拟机的新机安装和真实麦克风识别仍待验收**，暂不作完整场景稳定性的最终承诺。文件事务回滚不包含“新版本启动后自动健康检查并回退”。

请在 [Issues](https://github.com/Diorser/iSay/issues) 中描述 Windows 版本、iSay 版本、输入应用和复现步骤。提交日志前，请移除音频、转写正文、词库、账号、密钥和个人路径。

## 许可与作者

自有源码采用 [MIT License](./LICENSE)，作者 [Diorser](https://github.com/Diorser)。第三方软件、运行时、字体、商标和服务遵循各自许可与条款，MIT 不授予这些组件的再分发权，也不代表第三方授权或背书。详情见[隐私与第三方依赖](./docs/公开/隐私与第三方依赖.md)与[第三方依赖声明](./scripts/发布/第三方依赖声明.txt)。
