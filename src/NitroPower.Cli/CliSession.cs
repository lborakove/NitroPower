using NitroPower.Core;
using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.IO;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Nvidia;
using NitroPower.Core.Profiles;

namespace NitroPower.Cli;

/// <summary>
/// Where commands go: to nitropowerd when it runs (any member of the
/// nitropower group, no sudo), else straight to the hardware in this process
/// (root needed for changes). --direct forces the second.
/// </summary>
internal sealed class CliSession(string? profilesDir, string statePath, string presetsDir, string socketPath, bool direct)
    : IDisposable
{
    private NitroClient? _client;
    private LocalNitroApi? _local;
    private ProfileCatalog? _catalog;
    private bool _connected;

    public string SocketPath => socketPath;
    public NitroClient.ConnectFailure DaemonFailure { get; private set; } = NitroClient.ConnectFailure.NotRunning;

    /// <summary>True when commands go through nitropowerd.</summary>
    public bool UsesDaemon
    {
        get
        {
            Connect();
            return _client is not null;
        }
    }

    public INitroApi Api
    {
        get
        {
            Connect();
            return _client is not null ? _client : Local;
        }
    }

    public ProfileCatalog Catalog =>
        _catalog ??= ProfileCatalog.Load(MachineInfo.ProfileDirectories(profilesDir));

    /// <summary>Direct hardware access in this process (probe, watch, fallback).</summary>
    public LocalNitroApi Local
    {
        get
        {
            if (_local is not null)
                return _local;
            var fs = Sysfs.Real;
            var machine = MachineInfo.Detect(fs, Catalog);
            var controller = new NitroController(fs, machine, new StateStore(statePath),
                machine.NvidiaGpu is null ? null : new NvmlGpuControl(fs));
            return _local = new LocalNitroApi(controller, new PresetStore(presetsDir), new SensorReader(fs, () =>
            {
                using var nvml = NvmlSession.Open();
                return nvml.GetTelemetry();
            }));
        }
    }

    private void Connect()
    {
        if (_connected)
            return;
        _connected = true;
        if (direct)
            return;
        _client = NitroClient.TryConnect(socketPath, out var failure);
        DaemonFailure = failure;
    }

    /// <summary>A second connection for events (watch), or null without the daemon.</summary>
    public NitroClient? NewEventClient() =>
        direct ? null : NitroClient.TryConnect(socketPath, out _);

    /// <summary>Changes need the daemon, or root for direct access.</summary>
    public void RequireWrite(string what)
    {
        if (UsesDaemon || Environment.IsPrivilegedProcess)
            return;
        throw new NitroException(DaemonFailure == NitroClient.ConnectFailure.PermissionDenied
            ? $"{what}: you are not in the '{NitroApiProtocol.Group}' group. Run: sudo usermod -aG " +
              $"{NitroApiProtocol.Group} $USER, then log out and in (or use sudo)"
            : $"{what} needs nitropowerd running, or root: run it with sudo");
    }

    public void Dispose() => _client?.Dispose();
}
