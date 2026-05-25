# Desktop 前端完成情况检查

> 检查时间：2026-05-26

## 已完成 / 基本完成

| 模块 | 状态 | 说明 |
|---|---|---|
| ONNX 分类器推理 | 完成 | `OnnxImageClassifierService`：加载模型→张量转换→推理→softmax→Unicode 解码→Top-K |
| ONNX 嵌入向量推理 | 完成 | `OnnxImageEmbeddingService`：生成 768-dim 嵌入 → L2 归一化 |
| 图像预处理 | 完成 | `OnnxImageTensorFactory`：ImageSharp → resize 160×160 → NCHW float32 |
| 余弦相似度检索 | 完成 | `InMemoryEmbeddingIndexService`：全量数据集余弦相似度 → Top-K |
| 模型元数据加载 | 完成 | `OnnxModelMetadata`：读取 JSON（input/output name、labels、image_size） |
| Embedding 缓存 | 完成 | `JsonFileEmbeddingCacheService`：版本化 JSON 持久化，原子写入 |
| 数据集加载 | 完成 | `JsonLinesDevelopmentImageLibraryService`：读 metadata.jsonl + 本地图片 |
| Unicode 标签解码 | 完成 | `UnicodeLabelDecoder`："U+XXXX" → UTF-8 字符 |
| 启动初始化流程 | 完成 | `StartupController`：检查模型→加载元数据→加载/计算嵌入→建索引→进度上报 |
| 桌面 UI（MainView） | 完成 | 上传区 + 手写画板 + 结果显示（分类 + 相似图），双栏布局 |
| 手写画板 | 完成 | 224×224 画布，Polyline 自由绘制，清除按钮 |
| 手写/上传 Tab 切换 | 完成 | 左侧面板 Image Upload / Handwriting 选项卡 |
| 文件选择器 & 预览 | 完成 | 代码后置中通过 `StorageProvider` 实现 |
| Top-10 分类结果 | 完成 | 标签 + 置信度百分比 + 横向柱状图 |
| Top-10 相似图片 | 完成 | 图片网格 + 相似度徽章 + 标签 |
| 启动进度卡片 | 完成 | 初始化时显示进度条 |
| 空状态 / 加载状态 | 完成 | 无结果提示、分析中 loading 状态 |
| 状态指示器 | 完成 | 顶部绿色/红色圆点显示模型和索引就绪状态 |
| Predict 按钮 | 完成 | 初始化完成后显示，分析时禁用 |
| 品牌/主题样式 | 完成 | 自定义品牌色、阴影、卡片圆角 |
| MVVM 架构 | 完成 | CommunityToolkit.Mvvm，ViewLocator 自动绑定 |
| Desktop 入口 | 完成 | `Program.cs`：Classic Desktop Lifetime，Inter 字体 |
| Windows 清单 | 完成 | `app.manifest`：Win10 兼容 |
| Android 骨架 | 完成 | `MainActivity.cs` + `Application.cs`，但服务实现缺失 |

---

## 未完成 / 缺失

| 缺失项 | 严重度 | 说明 |
|---|---|---|
| **HuggingFace 模型下载服务** | 高 | 架构文档中的 `HuggingFaceDownloadService` 未实现，当前只用 `LocalDevelopmentModelAssetService` 检查本地文件 |
| **拖拽导入** | 中 | XAML 中定义了 `DropZoneBorder`，但 code-behind 未绑定 Drop/DragOver 事件 |
| **错误/Toast 通知系统** | 中 | 无用户可见的错误提示机制，仅依赖状态栏文本 `StatusText` |
| **单元测试** | 中 | 无测试项目，无任何测试文件 |
| **macOS/Linux 平台项目** | 中 | 架构文档提及 `.macOS` / `.Linux` 但未创建 |
| **MSIX 打包** | 中 | 未配置 Windows 应用打包项目 |
| **Android 平台实现** | 中 | `IPermissionService`（存储权限）、`IAppDataPathProvider`（Android 路径）的 Android 具体实现缺失 |
| **C# 原生 Parquet 读取** | 低 | 依赖 Python `build_dev_dataset_cache.py` 预处理为 JSONL，无 C# 直接读取能力 |
| **应用图标** | 低 | 使用默认 Avalonia 图标，未替换为品牌图标 |
| **动态 Batch 支持** | 低 | ONNX 模型支持动态 batch，但代码固定 batch=1 |
| **GPGPU/DirectML 加速** | 低 | 未指定 ONNX Runtime 执行提供程序，默认 CPU |

---

## 关键文件清单

```
KuzushiClassifierApp/
├── Controllers/
│   ├── StartupController.cs                  # 启动初始化编排
│   ├── ClassificationController.cs           # 分类管线
│   ├── SimilaritySearchController.cs         # 相似搜索管线
│   └── ImageAnalysisController.cs            # 组合分析
├── Services/
│   ├── OnnxImageClassifierService.cs         # ONNX 分类器
│   ├── OnnxImageEmbeddingService.cs          # ONNX 嵌入
│   ├── OnnxImageTensorFactory.cs             # 张量工厂
│   ├── OnnxModelMetadata.cs                  # 模型元数据
│   ├── UnicodeLabelDecoder.cs                # Unicode 解码
│   ├── InMemoryEmbeddingIndexService.cs      # 相似度索引
│   ├── EmbeddingSimilarity.cs                # 余弦相似度计算
│   ├── JsonFileEmbeddingCacheService.cs      # 嵌入缓存
│   ├── JsonLinesDevelopmentImageLibraryService.cs  # 数据集读取
│   ├── LocalDevelopmentModelAssetService.cs  # 本地模型
│   └── LocalDevelopmentBusinessServices.cs   # DI 注册
├── ViewModels/
│   ├── MainViewModel.cs                      # 主界面状态 + 全部用户操作
│   ├── ViewModelBase.cs                      # 基类
│   └── SimilarImageUiModel.cs                # 相似图缩略图
├── Views/
│   ├── MainView.axaml / .cs                  # 主界面 UI
│   └── MainWindow.axaml / .cs                # 窗口外壳
├── Models/                                   # 11 个数据模型
└── Platform/
    ├── IAppDataPathProvider.cs
    ├── IPermissionService.cs
    └── LocalDevelopmentAppDataPathProvider.cs
```

---

## 依赖与工具链

- **.NET** 10.0
- **Avalonia UI** 12.0.3 + Fluent Theme
- **CommunityToolkit.Mvvm** 8.4.0
- **Microsoft.ML.OnnxRuntime** 1.26.0
- **SixLabors.ImageSharp** 3.1.12
- **Python** 3.x（仅用于 `build_dev_dataset_cache.py`）
- **ONNX 模型**：convnext_tiny，160×160 输入，10596 类，768 维嵌入，来自 HuggingFace `kwadraten/hi-utokyo-kuzushi`
