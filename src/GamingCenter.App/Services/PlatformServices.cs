using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamingCenter.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GamingCenter.App.Services;

/// <summary>
/// Copies images into the local data folder, resized to at most 800 px and re-encoded,
/// so the database only stores a short relative path.
/// </summary>
public sealed class WpfImageStore(IDataPaths paths) : IImageStore
{
    private const int MaxSize = 800;

    public Task<string> ImportAsync(string sourceFile, string category, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(sourceFile)) throw new BusinessException("Image file not found.");
            if (new FileInfo(sourceFile).Length > 40 * 1024 * 1024) throw new BusinessException("Image is too large (max 40 MB).");

            BitmapSource frame;
            try
            {
                using var stream = File.OpenRead(sourceFile);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                frame = decoder.Frames[0];
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
            {
                throw new BusinessException("This file is not a supported image (use JPG, PNG, BMP or GIF).");
            }

            double scale = Math.Min(1.0, (double)MaxSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource output = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;

            bool hasAlpha = frame.Format.BitsPerPixel == 32 && frame.Format != PixelFormats.Bgr32;
            BitmapEncoder encoder = hasAlpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(output));

            var safeCategory = string.Concat(category.Where(char.IsLetterOrDigit));
            var dir = Path.Combine(paths.ImagesFolder, safeCategory);
            Directory.CreateDirectory(dir);
            var name = $"{Guid.NewGuid():N}{(hasAlpha ? ".png" : ".jpg")}";
            using (var fs = File.Create(Path.Combine(dir, name)))
                encoder.Save(fs);
            return $"{safeCategory}/{name}";
        }, ct);

    public string? Resolve(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        if (Path.IsPathRooted(relativePath)) return relativePath;
        var full = Path.GetFullPath(Path.Combine(paths.ImagesFolder, relativePath));
        return full.StartsWith(paths.ImagesFolder, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

public sealed class FileDialogService
{
    public string? OpenImage()
    {
        var dlg = new OpenFileDialog { Title = "Choose an image", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? SaveCsv(string defaultName)
    {
        var dlg = new SaveFileDialog { Title = "Export", Filter = "CSV (Excel)|*.csv", FileName = defaultName, AddExtension = true, DefaultExt = ".csv" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? OpenBackup(string? initialFolder)
    {
        var dlg = new OpenFileDialog { Title = "Choose a backup to restore", Filter = "Database backup|*.db|All files|*.*" };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder)) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickFolder(string? initial)
    {
        var dlg = new OpenFolderDialog { Title = "Choose backup folder" };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}

/// <summary>Minimal daily-rolling file logger in the data folder (logs\app-yyyyMMdd.log).</summary>
public sealed class FileLoggerProvider(string folder) : ILoggerProvider
{
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never crash the app */ }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) && logLevel < LogLevel.Warning) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            owner.Write(line);
        }
    }
}
