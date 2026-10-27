// Windows 10/11 native OCR integration using Windows.Media.Ocr.

using System.Diagnostics;
using System.Text;

namespace Filee.Platform.Windows;

internal static class WindowsOcr
{
    private static readonly string ScriptContent = @"param([string]$ImagePath)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.DataWriter, Windows.Storage.Streams, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
[void][Windows.Media.Ocr.OcrEngine, Windows.Media.Ocr, ContentType=WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | 
    Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 } | 
    Select-Object -First 1

function Await-Op($op, [Type]$t) {
    $m = $asTask.MakeGenericMethod($t)
    $task = $m.Invoke($null, @($op))
    $task.Wait()
    return $task.Result
}

$bytes = [System.IO.File]::ReadAllBytes($ImagePath)
$stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
$writer = [Windows.Storage.Streams.DataWriter]::new($stream)
$writer.WriteBytes($bytes)
$stored = Await-Op ($writer.StoreAsync()) ([uint32])
$writer.DetachStream() | Out-Null
$stream.Seek(0)

$dec = Await-Op ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bmp = Await-Op ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if ($eng -eq $null) {
    $eng = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US'))
}

$res = Await-Op ($eng.RecognizeAsync($bmp)) ([Windows.Media.Ocr.OcrResult])
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Write-Output $res.Text
";

    private static string? _scriptPath;
    private static readonly object _lock = new();

    private static string EnsureScript()
    {
        lock (_lock)
        {
            if (_scriptPath is not null && File.Exists(_scriptPath))
                return _scriptPath;

            var dir = Path.Combine(Path.GetTempPath(), "Filee");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "win_ocr_runner.ps1");
            File.WriteAllText(path, ScriptContent, Encoding.UTF8);
            _scriptPath = path;
            return path;
        }
    }

    public static async Task<string?> ExtractTextAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(imagePath))
            return null;

        try
        {
            var script = EnsureScript();
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(imagePath);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var text = (await stdoutTask).Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
