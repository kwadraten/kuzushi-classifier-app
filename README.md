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

启动测试：

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -- --repo-root . --take 10 --force
```

编译：

```powershell
dotnet build tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj
```

打包预构建数据：

```powershell
dotnet run --project tools\KuzushiPrebuilder\KuzushiPrebuilder.csproj -c Release -- --repo-root . --download-parallelism 2 --build-workers 4 --group-size 1000 --webp-quality 75 --max-width 123 --force
```
