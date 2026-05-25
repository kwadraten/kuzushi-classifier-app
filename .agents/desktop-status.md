# Desktop 前端完成情况检查

> 最后检查时间：2026-05-26（逐文件核实 + 本次修改验证）

## 已完成（代码实际存在且功能完整）

| 模块 | 状态 | 代码位置 | 核实说明 |
|---|---|---|---|
| ONNX 分类器推理 | ✅ 完成 | `Services/OnnxImageClassifierService.cs:7` | Session 创建 → Tensor → 推理 → Softmax → Unicode 解码 → Top-K |
| ONNX 嵌入向量推理 | ✅ 完成 | `Services/OnnxImageEmbeddingService.cs:7` | Session 创建 → Tensor → 推理 → L2 归一化 → ImageEmbedding |
| 图像预处理 | ✅ 完成 | `Services/OnnxImageTensorFactory.cs:9` | ImageSharp → Resize(160×160) → NCHW float32 |
| 余弦相似度检索 | ✅ 完成 | `Services/InMemoryEmbeddingIndexService.cs:5` | BuildAsync + SearchAsync → cosine → Top-K |
| 模型元数据加载 | ✅ 完成 | `Services/OnnxModelMetadata.cs:6` | JSON 反序列化（input_name/output_name/labels/image_size） |
| Embedding 缓存 | ✅ 完成 | `Services/JsonFileEmbeddingCacheService.cs:7` | 版本化 JSON（v1）→ 原子写入（temp+rename） |
| 数据集加载 | ✅ 完成 | `Services/JsonLinesDevelopmentImageLibraryService.cs:7` | 读取 metadata.jsonl → DatasetImage 列表 |
| Unicode 标签解码 | ✅ 完成 | `Services/UnicodeLabelDecoder.cs:3` | "U+XXXX" → `char.ConvertFromUtf32()` → UTF-8 字符 |
| 启动初始化流程 | ✅ 完成 | `Controllers/StartupController.cs:6` | 检查资产 → 加载元数据 → 加载/计算嵌入 → 建索引 → 进度上报 |
| **双模式路径解析** | ✅ 完成 | `Platform/AppDataPathProvider.cs:3` | 开发模式自动定位 `.agents/dev_data/`，生产模式使用安装目录 |
| **HuggingFace 模型下载** | ✅ 完成 | `Services/HuggingFaceModelAssetService.cs:8` | 实现 `IModelAssetService` + `IModelPathProvider`，缺少模型时从 HF Hub 下载 ONNX + metadata |
| **HuggingFace 数据集下载** | ✅ 完成 | `Services/HuggingFaceModelAssetService.cs:179` | 从 HF Hub API 查询 Parquet 文件列表 → 下载 → 自动扩展为 image cache |
| **C# 原生 Parquet 读取** | ✅ 完成 | `Services/HuggingFaceModelAssetService.cs:397` | Parquet.Net v4.25.0，支持 binary/struct 两种 image 列格式 |
| 无缓存时全量嵌入 | ✅ 完成 | `Controllers/StartupController.cs:83` | `for` 循环遍历全部 images，无限制；100 只是 Python smoke test 选项 |
| 桌面 UI（MainView） | ✅ 完成 | `Views/MainView.axaml:1` | 上传区 + 手写画板 + 双栏结果，响应式布局（760px 断点） |
| 手写画板 | ✅ 完成 | `Views/MainView.axaml.cs:161-195` | 224×224 Canvas + Polyline + 田字格辅助线 + 清除按钮 |
| 手写/上传 Tab 切换 | ✅ 完成 | `Views/MainView.axaml:58-77` | Segmented Control 风格，ActiveTab 绑定 |
| 文件选择器 & 预览 | ✅ 完成 | `Views/MainView.axaml.cs:129-158` | StorageProvider + FilePickerOpenOptions |
| 拖拽导入 | ✅ 完成 | `Views/MainView.axaml.cs:24-73` | AddHandler(DragOver + Drop)，支持多格式 IStorageItem/路径 |
| Top-10 分类结果 | ✅ 完成 | `Views/MainView.axaml:191-201` | ItemsControl + ProgressBar 置信度柱状图 + 百分比文本 |
| Top-10 相似图片 | ✅ 完成 | `Views/MainView.axaml:218-251` | WrapPanel 画廊 + 相似度徽章 + 标签栏 + 异步缩略图加载 |
| 启动进度卡片 | ✅ 完成 | `Views/MainView.axaml:139-145` | Border + ProgressBar + StartupStatusText 绑定 |
| 空状态 / 加载状态 | ✅ 完成 | `Views/MainView.axaml:181-188` | 无结果占位文本 / 分析中 IsIndeterminate 进度条 |
| 状态指示器 | ✅ 完成 | `Views/MainView.axaml:29-37` | 绿/红圆点（ModelStatusBrush / EmbeddingIndexStatusBrush） |
| Predict 按钮 | ✅ 完成 | `Views/MainView.axaml:148-163` | 初始化后显示，分析时禁用 + IsIndeterminate 进度 |
| 品牌/主题样式 | ✅ 完成 | `App.axaml:13-98` | BrandBlue、Card、Primary、Secondary 样式 + 过渡动画 |
| MVVM 架构 | ✅ 完成 | `ViewModels/ViewModelBase.cs` + `ViewLocator.cs` | CommunityToolkit.Mvvm + ObservableProperty + RelayCommand |
| Desktop 入口 | ✅ 完成 | `KuzushiClassifierApp.Desktop/Program.cs:6` | ClassicDesktopLifetime + Inter 字体 + Debug 工具 |
| Windows 清单 | ✅ 完成 | `KuzushiClassifierApp.Desktop/app.manifest:1` | Win10 兼容性（`supportedOS`） |
| Android 骨架 | ✅ 完成 | `KuzushiClassifierApp.Android/MainActivity.cs:14` | AvaloniaMainActivity，基础启动配置 |

---

## 未完成 / 缺失

| 缺失项 | 严重度 | 核实说明 |
|---|---|---|
| **错误/Toast 通知系统** | 🟡 中 | 无独立通知组件。错误通过 `StartupStatusText` / `AnalysisProgressText` 字符串属性显示 |
| **单元测试** | 🟡 中 | 无 `*Tests*` 目录或 `*.Test*.csproj`，无任何测试文件 |
| **macOS/Linux 平台项目** | 🟡 中 | 架构文档提及但无 `.macOS` / `.Linux` 的 `.csproj` 文件 |
| **MSIX 打包** | 🟡 中 | 无 Windows Application Packaging Project |
| **Android 平台实现** | 🟡 中 | `IPermissionService`（存储权限）、`IAppDataPathProvider`（Android 路径）的 Android 实现缺失 |
| **应用图标** | 🟢 低 | `Assets/avalonia-logo.ico` 为默认图标，未替换 |
| **动态 Batch 支持** | 🟢 低 | ONNX 模型含动态 batch，但 `OnnxImageTensorFactory` 固定 `new[] { 1, 3, imageSize, imageSize }` |
| **GPU 加速** | 🟢 低 | 无 `SessionOptions` 指定 DirectML/CUDA 执行提供程序，默认 CPU 推理 |
| **`IImagePicker` 接口实现** | 🟢 低 | 架构中定义但未实现，当前文件选择在 code-behind 直接调用 `StorageProvider` |

---

## 关键文件清单（已同步本次修改）

```
KuzushiClassifierApp/
├── Controllers/
│   ├── StartupController.cs                  # 启动初始化编排（全量嵌入，无 100 限制）
│   ├── ClassificationController.cs           # 分类管线
│   ├── SimilaritySearchController.cs         # 相似搜索管线
│   └── ImageAnalysisController.cs            # 并行分类 + 相似分析
├── Services/
│   ├── OnnxImageClassifierService.cs         # ONNX 分类器（IDisposable）
│   ├── OnnxImageEmbeddingService.cs          # ONNX 嵌入（L2 归一化）
│   ├── OnnxImageTensorFactory.cs             # ImageSharp→NCHW Tensor
│   ├── OnnxModelMetadata.cs                  # JSON 元数据 record
│   ├── UnicodeLabelDecoder.cs                # Unicode 标签静态解码
│   ├── InMemoryEmbeddingIndexService.cs      # 内存余弦相似度搜索
│   ├── EmbeddingSimilarity.cs                # 余弦相似度计算
│   ├── JsonFileEmbeddingCacheService.cs      # 版本化嵌入缓存（原子写入）
│   ├── JsonLinesDevelopmentImageLibraryService.cs  # JSONL 数据集读取
│   ├── HuggingFaceModelAssetService.cs       # HF 模型下载 + Parquet 扩展 (NEW)
│   ├── BusinessServices.cs                   # DI 组合根（生产+开发双模式）(NEW)
│   └── PassThroughImagePreprocessingService.cs    # 直通预处理器
├── ViewModels/
│   ├── MainViewModel.cs                      # 主界面 VM（引用 BusinessServices）
│   ├── ViewModelBase.cs                      # ObservableObject 基类
│   └── SimilarImageUiModel.cs                # 相似图异步缩略图加载
├── Views/
│   ├── MainView.axaml / .cs                  # 完整 UI（含 Canvas 渲染 + 响应式布局）
│   └── MainWindow.axaml / .cs                # Window 外壳
├── Models/                                   # 11 个纯数据模型
└── Platform/
    ├── IAppDataPathProvider.cs
    ├── IPermissionService.cs
    └── AppDataPathProvider.cs                # 双模式路径解析（替代旧 LocalDevelopment*）(NEW)
```

已删除的旧文件：
- `LocalDevelopmentAppDataPathProvider.cs` → 替换为 `AppDataPathProvider.cs`
- `LocalDevelopmentModelAssetService.cs` → 替换为 `HuggingFaceModelAssetService.cs`
- `LocalDevelopmentBusinessServices.cs` → 替换为 `BusinessServices.cs`

---

## 依赖与工具链

| 依赖 | 版本 |
|---|---|
| .NET | 10.0 |
| Avalonia UI + Fluent Theme | 12.0.3 |
| CommunityToolkit.Mvvm | 8.4.0 |
| Microsoft.ML.OnnxRuntime | 1.26.0 |
| SixLabors.ImageSharp | 3.1.12 |
| **Parquet.Net** | **4.25.0** (NEW) |

- **Python 3.x** — `build_dev_dataset_cache.py` 不再必需（C# Parquet.Net 可替代），但保留作为备选
- **ONNX 模型** — convnext_tiny backbone，160×160 输入，10596 类 (Unihan)，768 维嵌入
- **数据集来源** — HuggingFace `kwadraten/hi-utokyo-kuzushi`（5 个 Parquet shards，约 22k+ 张）

---

## 本次修改总结（2026-05-26）

1. **`AppDataPathProvider`** — 双模式路径解析：开发模式自动向上查找 `.agents/dev_data/`，生产模式使用 `AppContext.BaseDirectory`，运行时数据写入 `LocalApplicationData`
2. **`HuggingFaceModelAssetService`** — 同时实现 `IModelAssetService` 和 `IModelPathProvider`，无本地模型时从 HF Hub 下载 ONNX 文件，无数据集时下载 Parquet 并自动扩展为 image cache
3. **`BusinessServices`** — 统一 DI 组合根，使用新类替代所有 "LocalDevelopment" 前缀的旧类
4. **Parquet.Net v4.25.0** — 纯 C# Parquet 读取，支持 raw binary 和 nested struct 两种 HF 图片列格式
5. **全量嵌入** — 确认 `StartupController.BuildAndPersistEmbeddingsAsync` 已遍历全部数据集图片，无数量限制
6. **删除旧文件** — 移除 `LocalDevelopment*` 三个旧文件
7. **`architecture.md`** — 删除过时的 "Current Constraint" 骨架阶段声明
