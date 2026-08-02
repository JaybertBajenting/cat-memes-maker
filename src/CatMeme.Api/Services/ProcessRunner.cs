using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CatMeme.Api.Services;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    /// <summary>ffmpeg writes almost everything to stderr, so callers usually want both.</summary>
    public string All => StdOut + "\n" + StdErr;
}

/// <summary>
/// Runs external CLIs (ffmpeg, claude, codex) with full output capture, a hard timeout,
/// and a hook that exposes the OS process id so long renders can be cancelled.
/// </summary>
public class ProcessRunner(ILogger<ProcessRunner> log)
{
    // Native tools occasionally terminate inside a codec/filter before they can return a
    // useful exit code.  On Windows the default inherited error mode turns that into a modal
    // "Application Error" dialog, which blocks an unattended Hangfire worker indefinitely.
    // Keep failures non-interactive: RunAsync will still receive the non-zero process exit
    // and the normal retry/quarantine path can handle it.
    static ProcessRunner()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox); }
        catch (DllNotFoundException) { /* non-Windows test host despite the runtime check */ }
        catch (EntryPointNotFoundException) { /* very old/trimmed Windows host */ }
    }

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        TimeSpan? timeout = null,
        Action<int>? onStarted = null,
        string? stdin = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            // Without this, .NET decodes redirected output using the console's OEM codepage
            // (CP437/CP850 on this machine), so UTF-8 from the CLI is mangled on the way in:
            // an ellipsis arrives as "ΓÇª" and gets burned into the reel. Every curly quote,
            // dash and accent has the same problem.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Applied on top of the inherited environment, so callers only override what they
        // name. Used to switch off the AI CLI's extended thinking per invocation.
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var so = new StringBuilder();
        var se = new StringBuilder();
        // Bounded so a runaway process can't exhaust memory via log spam.
        const int cap = 512 * 1024;
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null && so.Length < cap) so.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null && se.Length < cap) se.AppendLine(e.Data); };

        log.LogDebug("exec {File} {Args}", fileName, string.Join(' ', psi.ArgumentList));

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        try { onStarted?.Invoke(proc.Id); } catch { /* reporting must never kill the run */ }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            var reason = timeoutCts.IsCancellationRequested ? "timed out" : "was cancelled";
            log.LogWarning("Process {File} (pid {Pid}) {Reason}", fileName, SafePid(proc), reason);
            return new ProcessResult(-1, so.ToString(), se + $"\nProcess {reason}.");
        }

        return new ProcessResult(proc.ExitCode, so.ToString(), se.ToString());
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    /// <summary>Kills a process by pid. Used by the cancel endpoint against a stored ffmpeg pid.</summary>
    public static bool KillByPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false; // process already exited or pid reused/invalid
        }
    }
}
