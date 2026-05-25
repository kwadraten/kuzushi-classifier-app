using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly StartupController _startupController;
    private readonly ImageAnalysisController _imageAnalysisController;
    private CancellationTokenSource? _startupCts;
    private CancellationTokenSource? _analysisCts;
    private byte[]? _selectedImageBytes;

    // --- Observable Properties ---

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _startupStatusText = "正在启动应用...";

    [ObservableProperty]
    private double _startupProgressFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelStatusBrush))]
    private bool _isModelReady;

    [ObservableProperty]
    private bool _isDatasetReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbeddingIndexStatusBrush))]
    private bool _isEmbeddingIndexReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UploadTabBackground))]
    [NotifyPropertyChangedFor(nameof(UploadTabForeground))]
    [NotifyPropertyChangedFor(nameof(HandwritingTabBackground))]
    [NotifyPropertyChangedFor(nameof(HandwritingTabForeground))]
    private int _activeTab; // 0 = File Upload, 1 = Handwriting Canvas

    [ObservableProperty]
    private bool _isUploadTabActive = true;

    [ObservableProperty]
    private bool _isHandwritingTabActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageSelected))]
    private string? _selectedImagePath;

    [ObservableProperty]
    private Bitmap? _selectedImagePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PredictButtonText))]
    private bool _isAnalyzing;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private string _analysisProgressText = "分析过程通常在 2 秒内完成。";

    // --- Derived Properties for Bindings ---

    public bool IsImageSelected => !string.IsNullOrEmpty(SelectedImagePath);

    public IBrush ModelStatusBrush => IsModelReady ? Brushes.Green : Brushes.Red;

    public IBrush EmbeddingIndexStatusBrush => IsEmbeddingIndexReady ? Brushes.Green : Brushes.Red;

    public IBrush UploadTabBackground => ActiveTab == 0 ? Brushes.White : Brushes.Transparent;
    public IBrush UploadTabForeground => ActiveTab == 0 ? SolidColorBrush.Parse("#005FAA") : SolidColorBrush.Parse("#404752");

    public IBrush HandwritingTabBackground => ActiveTab == 1 ? Brushes.White : Brushes.Transparent;
    public IBrush HandwritingTabForeground => ActiveTab == 1 ? SolidColorBrush.Parse("#005FAA") : SolidColorBrush.Parse("#404752");

    public string PredictButtonText => IsAnalyzing ? "正在分析预测..." : "开始识别预测";

    partial void OnActiveTabChanged(int value)
    {
        IsUploadTabActive = value == 0;
        IsHandwritingTabActive = value == 1;
    }

    public ObservableCollection<PredictionCandidate> PredictionCandidates { get; } = new();

    public ObservableCollection<SimilarImageUiModel> SimilarImages { get; } = new();

    // --- Constructors ---

    /// <summary>
    /// Parametrized constructor for runtime injection
    /// </summary>
    public MainViewModel(
        StartupController startupController,
        ImageAnalysisController imageAnalysisController)
    {
        _startupController = startupController;
        _imageAnalysisController = imageAnalysisController;
        
        // Auto start app initialization
        _ = InitializeAppAsync();
    }

    private static readonly BusinessServices DevServices = BusinessServices.Create();

    /// <summary>
    /// Parameterless constructor for XAML designer and standalone UI testing
    /// </summary>
    public MainViewModel() : this(DevServices.StartupController, DevServices.ImageAnalysisController)
    {
    }

    // --- Commands ---

    [RelayCommand]
    public async Task InitializeAppAsync()
    {
        _startupCts?.Cancel();
        _startupCts = new CancellationTokenSource();

        IsInitialized = false;
        IsModelReady = false;
        IsDatasetReady = false;
        IsEmbeddingIndexReady = false;
        StartupStatusText = "正在检查本地缓存与资源...";
        StartupProgressFraction = 0;

        try
        {
            var progress = new Progress<AssetPreparationProgress>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StartupStatusText = p.Message;
                    StartupProgressFraction = p.Fraction ?? 0;
                    
                    // Map step to indicators
                    if (p.Step > AssetPreparationStep.CheckingCache) IsModelReady = true;
                    if (p.Step > AssetPreparationStep.DownloadingDataset) IsDatasetReady = true;
                });
            });

            var result = await _startupController.PrepareAsync(progress, _startupCts.Token);

            IsModelReady = result.AssetStatus.ClassifierModelReady && result.AssetStatus.EmbeddingModelReady;
            IsDatasetReady = result.AssetStatus.DatasetReady;
            IsEmbeddingIndexReady = result.AssetStatus.EmbeddingIndexReady;
            StartupStatusText = $"初始化完成！已加载 {result.DatasetImageCount} 张数据集图片。";
            StartupProgressFraction = 1.0;
            IsInitialized = true;
        }
        catch (OperationCanceledException)
        {
            StartupStatusText = "初始化已被取消。";
        }
        catch (Exception ex)
        {
            StartupStatusText = $"初始化失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SelectImageAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            SelectedImagePath = path;
            _selectedImageBytes = await File.ReadAllBytesAsync(path);
            
            using var ms = new MemoryStream(_selectedImageBytes);
            SelectedImagePreview = new Bitmap(ms);

            ResetResults();
        }
        catch (Exception ex)
        {
            StartupStatusText = $"加载图片失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SetSelectedImageBytes(byte[] bytes)
    {
        _selectedImageBytes = bytes;
        SelectedImagePath = "Canvas_Handwritten.png";
        
        using var ms = new MemoryStream(bytes);
        SelectedImagePreview = new Bitmap(ms);

        ResetResults();
    }

    [RelayCommand]
    public void Reset()
    {
        SelectedImagePath = null;
        SelectedImagePreview = null;
        _selectedImageBytes = null;
        ResetResults();
    }

    [RelayCommand]
    public async Task AnalyzeAsync(byte[]? overrideBytes)
    {
        byte[]? targetBytes = overrideBytes ?? _selectedImageBytes;
        if (targetBytes == null || targetBytes.Length == 0)
        {
            AnalysisProgressText = "请先选择一张图片或手写绘制字形！";
            return;
        }

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();

        IsAnalyzing = true;
        AnalysisProgressText = "正在进行图像预处理...";
        PredictionCandidates.Clear();
        SimilarImages.Clear();
        HasResults = false;

        try
        {
            var kImage = KuzushiImage.FromBytes(targetBytes, SelectedImagePath ?? "input_image.png");

            await Task.Delay(500, _analysisCts.Token); // Give a bit of visual breathing time
            AnalysisProgressText = "正在通过ONNX模型预测文字候选...";

            var result = await _imageAnalysisController.AnalyzeAsync(kImage, 10, _analysisCts.Token);

            AnalysisProgressText = "正在计算特征相似度并匹配样本库...";

            // Populate Predictions
            foreach (var candidate in result.Prediction.Candidates)
            {
                PredictionCandidates.Add(candidate);
            }

            // Populate Similar Images UI Models
            var loadTasks = result.SimilarImages.Select(async similar =>
            {
                var uiModel = new SimilarImageUiModel(
                    similar.Image.Label,
                    similar.Similarity,
                    similar.Image.SourceUri,
                    similar.Image.LocalPath);
                
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SimilarImages.Add(uiModel));
                
                // Triggers async loading of the image thumbnail in the background
                await uiModel.LoadImageAsync(_analysisCts.Token).ConfigureAwait(false);
            });

            await Task.WhenAll(loadTasks);

            HasResults = true;
            AnalysisProgressText = "分析完成。";
        }
        catch (OperationCanceledException)
        {
            AnalysisProgressText = "分析已取消。";
        }
        catch (Exception ex)
        {
            AnalysisProgressText = $"分析失败: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    // --- Private Helpers ---

    private void ResetResults()
    {
        PredictionCandidates.Clear();
        SimilarImages.Clear();
        HasResults = false;
        AnalysisProgressText = "处理过程通常在 2 秒内完成。";
    }

}
