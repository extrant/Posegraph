using System.Collections.Concurrent;
using System.Diagnostics;

namespace Posegraph.Backend;

/// <summary>由插件启动的识别后端进程（python -m posegraph.app），收集输出供界面查看。</summary>
public sealed class BackendProcess : IDisposable
{
    private const int MaxLogLines = 200;

    private Process? _process;
    private readonly ConcurrentQueue<string> _log = new();

    public bool IsRunning => _process is { HasExited: false };

    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    public IEnumerable<string> Log => _log;

    /// <summary>检查配置，返回问题描述；为 null 表示可以启动。</summary>
    public static string? Validate(Configuration c)
    {
        if (string.IsNullOrWhiteSpace(c.BackendPython) || string.IsNullOrWhiteSpace(c.BackendDir))
            return Loc.T("尚未配置后端路径（设置页选择一键包目录，或分别填写 python.exe 与 backend 目录）");
        if (!File.Exists(c.BackendPython))
            return Loc.F("找不到 python：{0}", c.BackendPython);
        if (!File.Exists(Path.Combine(c.BackendDir, "src", "posegraph", "app.py")))
            return Loc.F("不是 backend 目录（缺少 src\\posegraph\\app.py）：{0}", c.BackendDir);
        return null;
    }

    /// <summary>一键包布局：&lt;根目录&gt;\python\python.exe 与 &lt;根目录&gt;\backend。成功时写入配置。</summary>
    public static bool TryUsePackage(Configuration c, string root)
    {
        var python = Path.Combine(root, "python", "python.exe");
        var backend = Path.Combine(root, "backend");
        if (!File.Exists(python) || !File.Exists(Path.Combine(backend, "src", "posegraph", "app.py"))) return false;
        c.BackendPython = python;
        c.BackendDir = backend;
        return true;
    }

    public void Start(Configuration c)
    {
        if (IsRunning) return;
        var psi = new ProcessStartInfo(c.BackendPython, "-m posegraph.app")
        {
            WorkingDirectory = c.BackendDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // 一键包里的 Python 没有安装 posegraph 包，直接从源码目录导入
        psi.Environment["PYTHONPATH"] = Path.Combine(c.BackendDir, "src");
        psi.Environment["PYTHONNOUSERSITE"] = "1";   // 不加载用户目录下的 site-packages，避免和用户自己装的包冲突
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["POSEGRAPH_PORT"] = c.BackendPort.ToString();
        psi.Environment["POSEGRAPH_BRIDGE_URL"] = $"http://127.0.0.1:{c.Port}";
        psi.Environment["POSEGRAPH_PRELOAD"] = c.PreloadModel ? "1" : "0";
        psi.Environment["POSEGRAPH_DEVICE"] = c.BackendDevice;
        psi.Environment["POSEGRAPH_RELEASE_AFTER"] = c.ReleaseAfter.ToString();
        psi.Environment["POSEGRAPH_MHR_ON_CPU"] = c.MhrOnCpu ? "1" : "0";
        psi.Environment["POSEGRAPH_USE_DEPTH"] = c.UseDepth ? "1" : "0";
        psi.Environment["POSEGRAPH_FAST"] = c.FastRecognition ? "1" : "0";
        psi.Environment["POSEGRAPH_FACE_DUMP"] = FaceAnimationDump.OutputDir;
        psi.Environment["POSEGRAPH_MIN_FREE_VRAM_MB"] = SystemProbe.RequiredVramMB.ToString();

        _log.Clear();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Append(e.Data);
        p.ErrorDataReceived += (_, e) => Append(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        Append(Loc.F("[Posegraph] 已启动后端 PID {0}", p.Id));
    }

    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process.Dispose();
        _process = null;
    }

    private void Append(string? line)
    {
        if (line is null) return;
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines && _log.TryDequeue(out _)) { }
    }

    public void Dispose() => Stop();
}
