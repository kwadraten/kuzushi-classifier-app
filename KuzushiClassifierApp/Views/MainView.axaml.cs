using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using KuzushiClassifierApp.ViewModels;

namespace KuzushiClassifierApp.Views;

public partial class MainView : UserControl
{
    private Polyline? _currentLine;

    public MainView()
    {
        InitializeComponent();
        
        // Add drag & drop handlers
        AddHandler(DragDrop.DragOverEvent, DragOverHandler);
        AddHandler(DragDrop.DropEvent, DropHandler);
    }

    private void DragOverHandler(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void DropHandler(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null && System.Linq.Enumerable.Any(files))
            {
                var localPath = System.Linq.Enumerable.First(files).Path.LocalPath;
                if (DataContext is MainViewModel vm)
                {
                    await vm.SelectImageAsync(localPath);
                }
            }
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width < 760)
        {
            // Switch to single-column (mobile layout)
            ResponsiveContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("*");
            ResponsiveContentGrid.RowDefinitions = RowDefinitions.Parse("Auto, Auto");
            
            Grid.SetColumn(LeftColumnPanel, 0);
            Grid.SetRow(LeftColumnPanel, 0);
            Grid.SetColumn(RightColumnPanel, 0);
            Grid.SetRow(RightColumnPanel, 1);
            
            LeftColumnPanel.Margin = new Thickness(0, 0, 0, 16);
            RightColumnPanel.Margin = new Thickness(0, 0, 0, 0);
        }
        else
        {
            // Switch to double-column (PC layout)
            ResponsiveContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("*, *");
            ResponsiveContentGrid.RowDefinitions = RowDefinitions.Parse("Auto");
            
            Grid.SetColumn(LeftColumnPanel, 0);
            Grid.SetRow(LeftColumnPanel, 0);
            Grid.SetColumn(RightColumnPanel, 1);
            Grid.SetRow(RightColumnPanel, 0);
            
            LeftColumnPanel.Margin = new Thickness(0, 0, 12, 0);
            RightColumnPanel.Margin = new Thickness(12, 0, 0, 0);
        }
    }

    private void OnDropZoneClick(object? sender, PointerPressedEventArgs e)
    {
        OnSelectFileClick(sender, new RoutedEventArgs());
    }

    private void OnUploadTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ActiveTab = 0;
        }
    }

    private void OnHandwritingTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ActiveTab = 1;
        }
    }

    private async void OnSelectFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择崩字单字图像",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            if (files.Count > 0)
            {
                var localPath = files[0].Path.LocalPath;
                if (DataContext is MainViewModel vm)
                {
                    await vm.SelectImageAsync(localPath);
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.StartupStatusText = $"打开文件失败: {ex.Message}";
            }
        }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(DrawingCanvas).Properties;
        if (properties.IsLeftButtonPressed)
        {
            var point = e.GetPosition(DrawingCanvas);
            _currentLine = new Polyline
            {
                Stroke = Brushes.Black,
                StrokeThickness = 8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Points = new Points { point }
            };
            DrawingCanvas.Children.Add(_currentLine);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_currentLine != null)
        {
            var point = e.GetPosition(DrawingCanvas);
            _currentLine.Points.Add(point);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _currentLine = null;
        e.Handled = true;
    }

    private void OnClearCanvasClick(object? sender, RoutedEventArgs e)
    {
        DrawingCanvas.Children.Clear();
        if (DataContext is MainViewModel vm)
        {
            vm.Reset();
        }
    }

    private async void OnPredictClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            if (vm.ActiveTab == 1)
            {
                // Capture handwriting strokes from Canvas
                byte[] canvasBytes = GetCanvasImageBytes();
                vm.SetSelectedImageBytes(canvasBytes);
                await vm.AnalyzeAsync(canvasBytes);
            }
            else
            {
                // Trigger standard file bytes analysis
                await vm.AnalyzeAsync(null);
            }
        }
    }

    public byte[] GetCanvasImageBytes()
    {
        // Canvas is fixed at 224x224 in XAML
        int width = 224;
        int height = 224;

        var renderTarget = new RenderTargetBitmap(new PixelSize(width, height));
        
        // Temporarily force update layout
        DrawingCanvas.Measure(new Size(width, height));
        DrawingCanvas.Arrange(new Rect(0, 0, width, height));
        
        renderTarget.Render(DrawingCanvas);

        using var ms = new MemoryStream();
        renderTarget.Save(ms);
        return ms.ToArray();
    }
}