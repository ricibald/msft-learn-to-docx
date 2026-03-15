using System.Diagnostics;

namespace MsftLearnToDocx.Services;

/// <summary>
/// Runs pandoc to convert markdown to DOCX.
/// </summary>
public sealed class PandocRunner
{
    /// <summary>
    /// Converts a markdown file to DOCX using pandoc.
    /// </summary>
    public void Convert(string markdownPath, string outputPath, string? templatePath, int tocDepth = 2)
    {
        var pandocPath = FindPandoc();

        var args = $"\"{markdownPath}\" -o \"{outputPath}\" --from=markdown --to=docx --wrap=none --toc --toc-depth={tocDepth}";

        if (!string.IsNullOrWhiteSpace(templatePath))
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException($"Template file not found: {templatePath}");
            args += $" --reference-doc=\"{templatePath}\"";
        }

        // Enable resource path for media resolution
        var resourcePath = Path.GetDirectoryName(markdownPath) ?? ".";
        args += $" --resource-path=\"{resourcePath}\"";

        Console.WriteLine($"Running pandoc...");
        var psi = new ProcessStartInfo
        {
            FileName = pandocPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = resourcePath
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pandoc process");

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pandoc failed (exit code {process.ExitCode}):\n{stderr}");

        if (!string.IsNullOrWhiteSpace(stderr))
            Console.WriteLine($"  pandoc warnings: {stderr}");
    }

    /// <summary>
    /// Verifies pandoc is installed and returns its path.
    /// </summary>
    public static string FindPandoc()
    {
        // Try "pandoc" in PATH
        var psi = new ProcessStartInfo
        {
            FileName = "pandoc",
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? "pandoc";
                    Console.WriteLine($"  Found: {firstLine}");
                    return "pandoc";
                }
            }
        }
        catch
        {
            // Not found in PATH
        }

        throw new InvalidOperationException(
            "pandoc not found. Please install it from https://pandoc.org/installing.html and ensure it's in your PATH.");
    }
}
