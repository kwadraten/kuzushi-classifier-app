# KuzushiClassifierApp

`KuzushiClassifierApp` 是一个基于 Avalonia UI、ONNX Runtime 和 Parquet 数据集构建的日语古籍崩字（Kuzushi）识别与相似样本匹配的高性能跨平台应用程序（支持桌面端与安卓端）。

---

## 项目结构 (Project Structure)

```text
├── KuzushiClassifierApp          # 共享核心业务逻辑（Model, View, ViewModel, Controllers, Services）
├── KuzushiClassifierApp.Desktop  # 桌面端（Windows/macOS/Linux）启动与平台实现
├── KuzushiClassifierApp.Android  # 安卓端（Android）启动与平台实现
├── .agents                       # 代理开发文档、元数据及开发数据缓存
└── Directory.Packages.props      # 集中 NuGet 包依赖版本控制
```

---

## 环境准备与依赖项 (Prerequisites)

- **.NET SDK**: 需要安装 **.NET 10.0 SDK** 或更高版本。
- **Android SDK (仅安卓端打包需要)**:
  - 命令行打包/运行安卓项目需要正确配置 Android SDK 环境变量（如 `ANDROID_HOME` 或 `ANDROID_SDK_ROOT`）。
  - 若系统未安装 Android SDK，编译安卓端时会遇到 `XA5300` 错误。

---

## 桌面端运行与打包指令 (Desktop Commands)

### 1. 本地运行 (开发调试)
在根目录下执行以下命令启动桌面端应用：
```bash
dotnet run --project KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj
```

### 2. 编译生成
```bash
dotnet build KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj
```

### 3. 发布与打包 (Release Packaging)
使用以下命令进行独立式（Self-contained）发布，生成无需本地安装 .NET 运行时的独立运行包：
- **Windows x64 端**:
  ```bash
  dotnet publish KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj -c Release -r win-x64 --self-contained
  ```
- **macOS x64 端**:
  ```bash
  dotnet publish KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj -c Release -r osx-x64 --self-contained
  ```
- **Linux x64 端**:
  ```bash
  dotnet publish KuzushiClassifierApp.Desktop/KuzushiClassifierApp.Desktop.csproj -c Release -r linux-x64 --self-contained
  ```
> 编译产物将保存在 `KuzushiClassifierApp.Desktop/bin/Release/net10.0/` 下的对应运行环境目录中。

---

## 安卓端运行与打包指令 (Android Commands)

> [!WARNING]
> 安卓端打包前请确保本机已安装 Android SDK 且配置好 SDK 路径。

### 1. 本地运行 (部署到设备/模拟器)
确保已有连接的安卓设备或运行中的模拟器，然后在根目录执行：
```bash
dotnet run --project KuzushiClassifierApp.Android/KuzushiClassifierApp.Android.csproj
```
或者使用以下 MSBuild 目标命令：
```bash
dotnet build KuzushiClassifierApp.Android/KuzushiClassifierApp.Android.csproj -t:Run
```

### 2. 编译生成
```bash
dotnet build KuzushiClassifierApp.Android/KuzushiClassifierApp.Android.csproj
```

### 3. 发布与生成 APK/AAB 包 (Release Packaging)
执行发布指令以生成适用于生产环境部署的发行版包（包含 APK 格式）：
```bash
dotnet publish KuzushiClassifierApp.Android/KuzushiClassifierApp.Android.csproj -c Release
```
> 发行包产物将输出在：
> `KuzushiClassifierApp.Android/bin/Release/net10.0-android/publish/` 目录中。

---

## 日志与诊断系统 (Logging & Diagnostics)

本项目集成了高性能、完全反射零开销（Reflection-free）的 **ZLogger** 结构化日志框架：
- **日志输出位置**：复用 `AppDataPathProvider` 逻辑，在应用数据缓存的 `logs` 子目录下写入。
  - **开发环境**：`.agents/dev_data/logs/`
  - **桌面生产环境**：`%LocalAppData%/KuzushiClassifierApp/logs/`
- **滚动与保留策略**：
  - 默认记录 `Information` 级别及以上日志。
  - 采用**每日滚动（Daily Roll）**策略，日志文件自动按日期命名（例如 `app_20260528_000.log`）。
  - 每次启动时自动进行日志清理，**默认仅保留最近 3 天**的日志文件。
- **异常安全原则**：禁止任何静默吞没异常的行为，所有被捕获的 Exception 必须显式通过 `ZLogger` 记录。
