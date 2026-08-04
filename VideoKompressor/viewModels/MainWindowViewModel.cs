using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoKompressor.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    // ---------- Status ----------

    [ObservableProperty]
    private string? status = "Ready";

    // ---------- Hardware ----------

    [ObservableProperty]
    private bool useCpu = true;

    [ObservableProperty]
    private bool useGpu;

    public List<string> GpuOptions { get; } =
        ["NVIDIA (NVENC)", "AMD (AMF)", "Intel (QuickSync)"];

    [ObservableProperty]
    private string selectedGpu = "NVIDIA (NVENC)";

    // ---------- Encoding ----------

    [ObservableProperty]
    private bool codecH264 = true;

    [ObservableProperty]
    private bool codecHevc;

    [ObservableProperty]
    private bool codecAv1;

    // ---------- Compression target ----------

    public List<string> Modes { get; } = ["Percentage", "Bit-rate", "File size"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSuffix))]
    private string selectedMode = "Percentage";

    public string TargetSuffix => SelectedMode switch
    {
        "Bit-rate"  => "kbps",
        "File size" => "MB",
        _           => "%",
    };

    [ObservableProperty]
    private string targetValue = "50";

    // ---------- Files ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputFileName))]
    private string? inputFilePath;

    public string InputFileName =>
        string.IsNullOrEmpty(InputFilePath) ? "No file selected" : Path.GetFileName(InputFilePath);

    // Plain string path to a temp .png — the View converts it to a Bitmap,
    // so the ViewModel never touches an Avalonia type.
    [ObservableProperty]
    private string? thumbnailPath;

    private string? _lastThumbnailFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFolderDisplay))]
    private string? outputFolder;

    public string OutputFolderDisplay =>
        string.IsNullOrEmpty(OutputFolder) ? "No output folder set" : OutputFolder;

    // "ffmpeg.exe" on Windows, plain "ffmpeg" on Linux/macOS.
    private static string FfmpegPath =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    // Generated hook from [ObservableProperty]: runs whenever InputFilePath is set.
    partial void OnInputFilePathChanged(string? value)
    {
        _ = GenerateThumbnailAsync(value);
    }

    // ---------- Thumbnail ----------

    private async Task GenerateThumbnailAsync(string? videoPath)
    {
        ThumbnailPath = null;

        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath) || !File.Exists(FfmpegPath))
            return;

        try
        {
            var duration = await GetDurationSecondsAsync(videoPath);
            var seek = duration > 1 ? duration * 0.10 : 0;
            var seekArg = seek.ToString("0.##", CultureInfo.InvariantCulture);

            var thumbPath = Path.Combine(Path.GetTempPath(), $"videokompressor_{Guid.NewGuid():N}.png");

            var arguments = $"-ss {seekArg} -i \"{videoPath}\" -frames:v 1 -vf scale=320:-1 -y \"{thumbPath}\"";

            var exitCode = await RunFfmpegAsync(arguments);

            // Only apply if the user hasn't picked a different file in the meantime.
            if (exitCode == 0 && File.Exists(thumbPath) && videoPath == InputFilePath)
            {
                DeleteQuietly(_lastThumbnailFile);
                _lastThumbnailFile = thumbPath;
                ThumbnailPath = thumbPath;
            }
        }
        catch
        {
            // Ignores errors incase thumbnail unavailable. The user can still compress the video.
        }
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try { File.Delete(path); } catch { /* temp file, best effort */ }
    }

    // ---------- Compress ----------

    [RelayCommand]
    private async Task Compress()
    {
        if (string.IsNullOrEmpty(InputFilePath) || !File.Exists(InputFilePath))
        {
            Status = "Select a video file first.";
            return;
        }

        if (string.IsNullOrEmpty(OutputFolder))
        {
            Status = "Set an output folder first.";
            return;
        }

        if (!File.Exists(FfmpegPath))
        {
            Status = $"ffmpeg not found at: {FfmpegPath}";
            return;
        }

        if (!double.TryParse(TargetValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var target) || target <= 0)
        {
            Status = "Enter a valid target value.";
            return;
        }

        var inputInfo = new FileInfo(InputFilePath);

        Status = "Reading video info...";
        var durationSeconds = await GetDurationSecondsAsync(InputFilePath);
        if (durationSeconds <= 0)
        {
            Status = "Could not read the video's duration.";
            return;
        }

        const int audioKbps = 128;

        var videoKbps = SelectedMode switch
        {
            // User typed the video bit-rate directly.
            "Bit-rate"  => (int)target,

            // User typed a target size in MB.
            "File size" => TargetBytesToVideoKbps(target * 1024 * 1024, durationSeconds, audioKbps),

            // Percentage: target is a fraction of the input file's size.
            _           => TargetBytesToVideoKbps(inputInfo.Length * (target / 100.0), durationSeconds, audioKbps),
        };
        videoKbps = Math.Max(videoKbps, 50); // floor so a tiny target doesn't produce garbage args

        var encoder = PickEncoder();
        var outputPath = Path.Combine(
            OutputFolder,
            Path.GetFileNameWithoutExtension(InputFilePath) + "_compressed.mp4");

        var arguments =
            $"-y -i \"{InputFilePath}\" -c:v {encoder} -b:v {videoKbps}k -c:a aac -b:a {audioKbps}k \"{outputPath}\"";

        Status = $"Compressing ({encoder}, {videoKbps} kbps)...";

        var exitCode = await RunFfmpegAsync(arguments);

        if (exitCode == 0 && File.Exists(outputPath))
        {
            var inMb = inputInfo.Length / 1024.0 / 1024.0;
            var outMb = new FileInfo(outputPath).Length / 1024.0 / 1024.0;
            Status = $"Done: {inMb:F1} MB → {outMb:F1} MB";
        }
        else
        {
            Status = "Compression failed.";
        }
    }

    // ---------- Helpers ----------

    /// <summary>Maps the UI selections to an ffmpeg encoder name.</summary>
    private string PickEncoder()
    {
        var codec = CodecHevc ? "hevc" : CodecAv1 ? "av1" : "h264";

        if (UseCpu)
        {
            return codec switch
            {
                "hevc" => "libx265",
                "av1"  => "libsvtav1",
                _      => "libx264",
            };
        }

        var vendorSuffix =
            SelectedGpu.Contains("NVIDIA") ? "nvenc" :
            SelectedGpu.Contains("AMD")    ? "amf"   :
                                             "qsv";

        return $"{codec}_{vendorSuffix}"; // e.g. h264_nvenc, h265_nvenc, av1_nvenc
    }

    /// <summary>Given a target size in bytes, works out the video bit-rate that lands near it.</summary>
    private static int TargetBytesToVideoKbps(double targetBytes, double durationSeconds, int audioKbps)
    {
        var totalKbps = targetBytes * 8 / durationSeconds / 1000;
        return (int)(totalKbps - audioKbps);
    }

    /// <summary>
    /// Runs "ffmpeg -i input" with no output file. ffmpeg prints the file's metadata
    /// (including its duration) to stderr before erroring out, which is all we need.
    /// Saves bundling ffprobe for now.
    /// </summary>
    private static async Task<double> GetDurationSecondsAsync(string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = $"-i \"{inputPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var match = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!match.Success)
            return 0;

        return int.Parse(match.Groups[1].Value) * 3600
             + int.Parse(match.Groups[2].Value) * 60
             + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> RunFfmpegAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = arguments,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Console.WriteLine(e.Data); //ts is for the progress bar that I'll implement later.
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return process.ExitCode;
    }
}
