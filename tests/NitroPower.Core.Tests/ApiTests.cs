using NitroPower.Core.Api;
using NitroPower.Core.Control;
using NitroPower.Core.Hardware;
using NitroPower.Core.Monitoring;
using NitroPower.Core.Policy;
using NitroPower.Core.Profiles;

namespace NitroPower.Core.Tests;

public class ApiTests
{
    /// <summary>A daemon-like server on a temporary socket, over a fake ANV15-52.</summary>
    private sealed class Server : IDisposable
    {
        public FakeNitro Machine { get; }
        public RpcServer Rpc { get; }
        public string Socket { get; }
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public Server(FakeNitro machine)
        {
            Machine = machine;
            Socket = Path.Join(machine.Tree.Root, "run", "nitropower.sock");
            var c = machine.Controller();
            var api = new LocalNitroApi(c, new PresetStore(Path.Join(machine.Tree.Root, "presets")),
                new SensorReader(machine.Tree.Fs), daemon: true);
            Rpc = new RpcServer(Socket, new RpcDispatcher(api), new object(), _ => { });
            Rpc.Mutated = method => Rpc.Broadcast(new NitroEvent("changed") { Method = method });
            Rpc.Start(groupId: null);
            _task = Rpc.RunAsync(_stop.Token);
        }

        public NitroClient Connect() =>
            NitroClient.TryConnect(Socket, out _) ?? throw new InvalidOperationException("no server");

        public void Dispose()
        {
            _stop.Cancel();
            _task.Wait(2000);
            Rpc.Dispose();
            Machine.Dispose();
        }
    }

    [Fact]
    public void Client_drives_the_hardware_through_the_socket()
    {
        using var server = new Server(new FakeNitro("balanced"));
        using var client = server.Connect();

        var info = client.Info();
        Assert.True(info.Daemon);
        Assert.Equal(NitroApiProtocol.Version, info.Protocol);

        var status = client.Status();
        Assert.Equal("balanced", status.Mode);
        Assert.Equal(["eco", "quiet", "balanced", "performance"], status.Modes.Select(m => m.Id));
        Assert.Equal(new TurboConfig("performance", "max"), status.TurboConfig);
        Assert.True(status.Features[NitroFeatures.KeyboardTimeout]);
        Assert.Equal("10de:28a0", status.Gpu!.PciId);
        Assert.Equal(2, status.Modes.Single(m => m.Id == "performance").GpuOcLevel);
        Assert.Equal(["quiet", "balanced", "performance"], status.ModeCycle!.Ac);
        Assert.Equal(new GpuClockOffsets(100, 200), status.Gpu.Levels![2]);
        Assert.False(status.Gpu.CustomLevels);

        client.SetGpuLevels([new(0, 0), new(0, 0), new(120, 300)]);
        Assert.True(client.Status().Gpu!.CustomLevels);
        client.SetGpuLevels(null);
        Assert.False(client.Status().Gpu!.CustomLevels);

        var turbo = client.ToggleTurbo();
        Assert.Equal((true, "performance", "max"), (turbo.Turbo, turbo.To, turbo.Fans));
        Assert.Equal("100,100", server.Machine.Fans);
        Assert.True(client.Status().Turbo);

        Assert.Equal("quiet", client.SetMode("quiet").To);
        client.SetFeature(NitroFeatures.BatteryLimiter, true);
        Assert.Equal("1", server.Machine.Attr("battery_limiter"));
        client.SetFanCurve(new FanCurveSettings([new(40, 20), new(90, 100)], [new(40, 20), new(90, 100)]));
        Assert.Equal("curve", client.Status().FanChoice);

        client.SavePreset("test");
        Assert.Equal(["test"], client.ListPresets());
        Assert.Equal("quiet", client.GetPreset("test")!.Mode);
        Assert.Null(client.GetPreset("missing"));
    }

    [Fact]
    public void Errors_come_back_as_exceptions_with_the_message()
    {
        using var server = new Server(new FakeNitro("balanced", onAc: false));
        using var client = server.Connect();

        Assert.Contains("AC", Assert.Throws<NitroException>(() => client.SetTurbo(true)).Message);
        Assert.Contains("unknown mode", Assert.Throws<NitroException>(() => client.SetMode("warp")).Message);
        Assert.Contains("unknown feature", Assert.Throws<NitroException>(() => client.SetFeature("x", true)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyboardEffectState(KeyboardEffect.Static, 0, 500,
            KeyboardDirection.None, Rgb.Black).Validate());
        Assert.Contains("four_zoned_kb", Assert.Throws<NitroException>(() =>
            client.SetKeyboardZones(PerZoneColors.Uniform(new Rgb(1, 2, 3), 50))).Message);

        // the connection is still usable after errors
        Assert.Equal("balanced", client.Status().Mode);
    }

    [Fact]
    public async Task Subscribers_receive_events_after_changes()
    {
        using var server = new Server(new FakeNitro("balanced"));
        using var events = server.Connect();
        using var client = server.Connect();
        var received = new List<NitroEvent>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = Task.Run(() => events.Subscribe(e =>
        {
            lock (received)
                received.Add(e);
            if (e.Method == "mode.set")
                stop.Cancel();
        }, stop.Token));

        SpinWait.SpinUntil(() => server.Rpc.ClientCount == 2, 2000);
        Thread.Sleep(100); // let the subscription land
        client.SetMode("quiet");
        await listener.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
            Assert.Contains(received, e => e is { Type: "changed", Method: "mode.set" });
    }

    [Fact]
    public void Dispatcher_follows_json_rpc()
    {
        using var m = new FakeNitro();
        var api = new LocalNitroApi(m.Controller(), new PresetStore(Path.Join(m.Tree.Root, "p")), new SensorReader(m.Tree.Fs));
        var d = new RpcDispatcher(api);

        Assert.Contains("-32700", d.Handle("{not json").Response);
        Assert.Contains("-32601", d.Handle("""{"jsonrpc":"2.0","id":1,"method":"nope"}""").Response);
        Assert.Contains("-32602", d.Handle("""{"jsonrpc":"2.0","id":2,"method":"mode.set","params":{}}""").Response);

        var reply = d.Handle("""{"jsonrpc":"2.0","id":"a","method":"mode.set","params":{"mode":"quiet"}}""");
        Assert.True(reply.Mutating);
        Assert.Contains("\"id\":\"a\"", reply.Response);
        Assert.Contains("\"to\":\"quiet\"", reply.Response);

        var notification = d.Handle("""{"jsonrpc":"2.0","method":"mode.set","params":{"mode":"balanced"}}""");
        Assert.Null(notification.Response); // no id: no answer, but it ran
        Assert.Equal("balanced", m.Mode);

        Assert.False(d.Handle("""{"jsonrpc":"2.0","id":3,"method":"status.get"}""").Mutating);
    }

    [Fact]
    public void Oversized_requests_and_extra_clients_are_cut_off()
    {
        using var server = new Server(new FakeNitro("balanced"));

        using (var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                   System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            raw.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(server.Socket));
            var junk = new byte[RpcServer.MaxLineBytes + 16 * 1024];
            Array.Fill(junk, (byte)'x');
            try
            {
                raw.Send(junk); // no newline: the server must not keep buffering
            }
            catch (System.Net.Sockets.SocketException)
            {
                // closed while sending: also fine
            }
            raw.ReceiveTimeout = 5000;
            try
            {
                Assert.Equal(0, raw.Receive(new byte[16])); // closed by the server
            }
            catch (System.Net.Sockets.SocketException e)
            {
                // or reset, because the server closed with our bytes unread
                Assert.Equal(System.Net.Sockets.SocketError.ConnectionReset, e.SocketErrorCode);
            }
        }

        var clients = new List<NitroClient>();
        try
        {
            for (var i = 0; i < RpcServer.MaxClients; i++)
                clients.Add(server.Connect());
            Assert.Equal("balanced", clients[^1].Status().Mode);
            using var extra = server.Connect();
            Assert.ThrowsAny<Exception>(() => extra.Info());
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
        SpinWait.SpinUntil(() => server.Rpc.ClientCount == 0, 2000);
        using var again = server.Connect();
        Assert.Equal("balanced", again.Status().Mode);
    }

    [Fact]
    public void Missing_socket_means_the_daemon_is_not_running()
    {
        using var tree = new TestTree();
        Assert.Null(NitroClient.TryConnect(Path.Join(tree.Root, "none.sock"), out var failure));
        Assert.Equal(NitroClient.ConnectFailure.NotRunning, failure);
    }

    [Fact]
    public void Group_lookup_reads_etc_group()
    {
        using var tree = new TestTree().File("/group", "root:x:0:\nnitropower:x:986:alice\n");
        Assert.Equal(986, RpcServer.FindGroupId("nitropower", Path.Join(tree.Root, "group")));
        Assert.Null(RpcServer.FindGroupId("wheel", Path.Join(tree.Root, "group")));
    }
}
