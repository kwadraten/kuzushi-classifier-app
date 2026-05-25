using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KuzushiClassifierApp.ViewModels;

public sealed partial class SimilarImageUiModel : ObservableObject
{
    private static readonly HttpClient HttpClient = new();

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoadingImage;

    public string Label { get; }
    public float Similarity { get; }
    public string MatchText => $"{(int)Math.Round(Similarity * 100)}% 匹配";
    public string? SourceUri { get; }
    public string? LocalPath { get; }

    public SimilarImageUiModel(string label, float similarity, string? sourceUri, string? localPath)
    {
        Label = label;
        Similarity = similarity;
        SourceUri = sourceUri;
        LocalPath = localPath;
    }

    public async Task LoadImageAsync(KuzushiClassifierApp.Services.IImageLibraryService? libraryService, CancellationToken cancellationToken = default)
    {
        IsLoadingImage = true;
        try
        {
            byte[] bytes;

            if (libraryService != null)
            {
                var datasetImage = new KuzushiClassifierApp.Models.DatasetImage(
                    Id: string.Empty,
                    Label: Label,
                    SourceUri: SourceUri,
                    LocalPath: LocalPath
                );
                var kuzushiImage = await libraryService.LoadImageAsync(datasetImage, cancellationToken).ConfigureAwait(false);
                bytes = kuzushiImage.Bytes;
            }
            // Check if local file exists on disk first
            else if (!string.IsNullOrEmpty(LocalPath) && File.Exists(LocalPath))
            {
                bytes = await File.ReadAllBytesAsync(LocalPath, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(SourceUri))
            {
                // Fallback to HTTP download
                bytes = await HttpClient.GetByteArrayAsync(SourceUri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                IsLoadingImage = false;
                return;
            }

            // Create Bitmap from byte array
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);

            // Update on UI thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Image = bitmap;
                IsLoadingImage = false;
            });
        }
        catch (Exception)
        {
            IsLoadingImage = false;
            // Fallback: stay as null so the UI shows placeholder
        }
    }
}
