using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace NzbDrone.Core.Plugins;

internal class FFMPEG
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const int DefaultTimeoutMs = 300000; // 5 minutes for large files

    public static string[] ProbeCodecs(string filePath)
    {
        Logger.Debug($"FFMPEG: Probing codecs for {filePath}");
        var (exitCode, output, errorOutput, args) = Call("ffprobe", $"-select_streams a -show_entries stream=codec_name:stream_tags=language -of default=nk=1:nw=1 {EncodeParameterArgument(filePath)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Probing codecs failed\n{args}\n{errorOutput}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static void ConvertWithoutReencode(string input, string output)
    {
        Logger.Info($"FFMPEG: Converting {input} to {output} (copy codec)");
        var (exitCode, _, errorOutput, args) = Call("ffmpeg", $"-y -i {EncodeParameterArgument(input)} -vn -acodec copy {EncodeParameterArgument(output)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Conversion without re-encode failed\n{args}\n{errorOutput}");
        Logger.Info($"FFMPEG: Conversion complete: {output}");
    }

    public static void Reencode(string input, string output, int bitrate)
    {
        Logger.Info($"FFMPEG: Re-encoding {input} to {output} at {bitrate}kbps");
        var (exitCode, _, errorOutput, args) = Call("ffmpeg", $"-y -i {EncodeParameterArgument(input)} -b:a {bitrate}k {EncodeParameterArgument(output)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Re-encoding failed\n{args}\n{errorOutput}");
        Logger.Info($"FFMPEG: Re-encoding complete: {output}");
    }

    private static (int exitCode, string output, string errorOutput, string arg) Call(string executable, string arguments)
    {
        var output = new StringBuilder();
        var errorOutput = new StringBuilder();
        var outputClosed = new System.Threading.ManualResetEventSlim(false);
        var errorClosed = new System.Threading.ManualResetEventSlim(false);

        using var proc = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true
        };

        // Use async event handlers to prevent deadlock
        // The deadlock occurs when stdout/stderr buffers fill up and block the child process
        // while the parent is waiting on ReadToEnd() which waits for the child to exit
        proc.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
                output.AppendLine(e.Data);
            else
                outputClosed.Set(); // null signals end of stream
        };

        proc.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
                errorOutput.AppendLine(e.Data);
            else
                errorClosed.Set(); // null signals end of stream
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Wait for process to exit with timeout
            if (!proc.WaitForExit(DefaultTimeoutMs))
            {
                Logger.Warn($"FFMPEG: Process timed out after {DefaultTimeoutMs / 1000} seconds, killing process");
                try
                {
                    proc.Kill(true); // Kill entire process tree (.NET 5+)
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "FFMPEG: Failed to kill timed-out process");
                }
                throw new FFMPEGException($"FFMPEG timed out after {DefaultTimeoutMs / 1000} seconds: {arguments}");
            }

            // Wait for async output handlers to complete (they may still be processing)
            // Use a short timeout here since the process has already exited
            outputClosed.Wait(5000);
            errorClosed.Wait(5000);

            return (proc.ExitCode, output.ToString(), errorOutput.ToString(), arguments);
        }
        catch (Exception ex) when (ex is not FFMPEGException)
        {
            Logger.Error(ex, $"FFMPEG: Failed to execute {executable}");
            throw new FFMPEGException($"Failed to execute {executable}: {ex.Message}", ex);
        }
    }

    private static string EncodeParameterArgument(string original)
    {
        if (string.IsNullOrEmpty(original))
            return original;

        var value = Regex.Replace(original, @"(\\*)" + "\"", @"$1\$0");
        value = Regex.Replace(value, @"^(.*\s.*?)(\\*)$", "\"$1$2$2\"");
        return value;
    }
}

public class FFMPEGException : Exception
{
    public FFMPEGException() { }
    public FFMPEGException(string message) : base(message) { }
    public FFMPEGException(string message, Exception inner) : base(message, inner) { }
}
