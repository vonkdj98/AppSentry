using System.Diagnostics;
using System.Text;

namespace AppSentry.Core.Util;

/// <summary>
/// Runs a console program with a timeout that actually works: stdout and stderr are drained
/// asynchronously (so a full stderr pipe can't deadlock the child), and a child that overruns
/// its timeout is killed instead of hanging the scan forever.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(bool Started, bool TimedOut, int? ExitCode, string StdOut, string StdErr)
    {
        public bool Succeeded => Started && !TimedOut && ExitCode == 0;
    }

    public static Result Run(string fileName, string arguments, TimeSpan timeout, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return new Result(false, false, null, "", "");
        }
        catch (Exception)
        {
            return new Result(false, false, null, "", "");
        }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeout))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            EngineLog.Warn($"{fileName} {arguments} timed out after {timeout.TotalSeconds:0}s and was killed");
            return new Result(true, true, null, SafeResult(stdout), SafeResult(stderr));
        }

        // The timed wait doesn't wait for the async readers to hit EOF; this overload does.
        proc.WaitForExit();
        return new Result(true, false, proc.ExitCode, SafeResult(stdout), SafeResult(stderr));
    }

    private static string SafeResult(Task<string> task)
    {
        try { return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : ""; }
        catch { return ""; }
    }
}
