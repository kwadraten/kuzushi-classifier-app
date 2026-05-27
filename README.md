# Kuzushi Classifier App

## 项目组成

```text
KuzushiClassifierApp/              共享业务逻辑、模型、服务和 Avalonia 视图
KuzushiClassifierApp.Desktop/      桌面端启动项目
KuzushiClassifierApp.Android/      Android 端启动项目
tools/KuzushiPrebuilder/           开发者预构建数据包工具
```

## 还原依赖

```powershell
dotnet restore KuzushiClassifierApp.slnx
```

## 桌面端

启动：

```powershell
dotnet run --project KuzushiClassifierApp.Desktop\KuzushiClassifierApp.Desktop.csproj
```

编译：

```powershell
dotnet build KuzushiClassifierApp.Desktop\KuzushiClassifierApp.Desktop.csproj
```

打包：

```powershell
dotnet publish KuzushiClassifierApp.Desktop\KuzushiClassifierApp.Desktop.csproj -c Release -o artifacts\desktop
```

## Android 端

编译：

```powershell
dotnet build KuzushiClassifierApp.Android\KuzushiClassifierApp.Android.csproj -c Release
```

安装并启动：

```powershell
dotnet build KuzushiClassifierApp.Android\KuzushiClassifierApp.Android.csproj -t:Run -c Debug
```

打包：

```powershell
dotnet publish KuzushiClassifierApp.Android\KuzushiClassifierApp.Android.csproj -c Release -o artifacts\android
```

## 预构建工具

开发环境的 `.agents/dev_data/prebuilt` 仅用于生成和检查预构建产物，运行时不能直接从该目录读取数据。应用启动时的资源加载顺序是：先准备模型，再准备 HuggingFace 数据集 shard，最后下载并解包远端 DiskANN 索引 tar。远端 tar 只打包 `manifest.json` 和 `vectors/dotvector-shikiji-diskann/`；匹配结果里的图片继续从本地 shard 读取，不从预构建 tar 或开发目录读取。

启动测试：

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -- --repo-root . --take 10 --force
```

编译：

```powershell
dotnet build tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj
```

打包预构建 DiskANN 索引：

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -c Release -- --repo-root . --download-parallelism 2 --build-workers 4 --group-size 1000 --webp-quality 75 --max-width 123 --force
```
