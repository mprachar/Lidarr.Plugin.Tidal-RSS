using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Plugins;

internal class FFMPEG
{
    public static string[] ProbeCodecs(string filePath)
    {
        var (exitCode, output, errorOutput, a) = Call("ffprobe", $"-select_streams a -show_entries stream=codec_name:stream_tags=language -of default=nk=1:nw=1 {EncodeParameterArgument(filePath)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Probing codecs failed\n{a}\n{errorOutput}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static void ConvertWithoutReencode(string input, string output)
    {
        var (exitCode, _, errorOutput, a) = Call("ffmpeg", $"-y -i {EncodeParameterArgument(input)} -vn -acodec copy {EncodeParameterArgument(output)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Conversion without re-encode failed\n{a}\n{errorOutput}");
    }

    public static void Reencode(string input, string output, int bitrate)
    {
        var (exitCode, _, errorOutput, a) = Call("ffmpeg", $"-y -i {EncodeParameterArgument(input)} -b:a {bitrate}k {EncodeParameterArgument(output)}");
        if (exitCode != 0)
            throw new FFMPEGException($"Re-encoding failed\n{a}\n{errorOutput}");
    }

    private static (int exitCode, string output, string errorOutput, string arg) Call(string executable, string arguments)
    {
        using var proc = new Process()
        {
            StartInfo = new()
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        proc.Start();
        var output = proc.StandardOutput.ReadToEnd();
        var errorOutput = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit(60000))
            proc.Kill();

        return (proc.ExitCode, output, errorOutput, arguments);
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
