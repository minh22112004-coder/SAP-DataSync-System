using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SapDataSync.Launcher.Services;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string ErrorMessage => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput.Trim()
        : StandardError.Trim();
}

public sealed class CommandRunner
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new CommandResult(-1, string.Empty, $"Không thể khởi chạy {fileName}.");
            }
        }
        catch (Win32Exception exception)
        {
            return new CommandResult(-1, string.Empty, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the checks.
            }

            throw;
        }

        return new CommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }
}
