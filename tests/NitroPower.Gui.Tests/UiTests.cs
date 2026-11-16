using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NitroPower.Gui.Controls;
using NitroPower.Gui.Localization;
using NitroPower.Gui.Services;
using NitroPower.Gui.ViewModels;
using NitroPower.Gui.Views;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NitroPower.Gui.Tests;

public static class TestEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

public sealed class HeadlessFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestEntry));

    public void Dispose() => Session.Dispose();
}

/// <summary>The real window over the demo API, driven with headless input.</summary>
public class UiTests(HeadlessFixture fixture) : IClassFixture<HeadlessFixture>
{
    private sealed record Ui(MainWindow Window, MainViewModel Vm, NitroSession Nitro, string Dir);

    private T Run<T>(Func<Ui, Task<T>> test) => fixture.Session.Dispatch(async () =>
    {
        var dir = Directory.CreateTempSubdirectory("nitropower-ui-").FullName;
        var nitro = new NitroSession("/nonexistent", demo: true);
        var vm = new MainViewModel(nitro, Path.Join(dir, "gui.json"));
        vm.UpdateSettings(s => s with { Language = "pt" });
        var window = new MainWindow { DataContext = vm, Width = 1220, Height = 1500 };
        nitro.Start();
        await nitro.RefreshStatus();
        window.Show();
        await Settle();
        try
        {
            return await test(new Ui(window, vm, nitro, dir));
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
            nitro.Dispose();
            Loc.Instance.SetLanguage(Loc.English);
            Directory.Delete(dir, recursive: true);
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
    }

    private static async Task Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await Settle();
    }

    private static IEnumerable<T> All<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>();

    private static bool HasText(Visual root, string text) =>
        All<TextBlock>(root).Any(t => t.Text == text && t.IsEffectivelyVisible);

    [Fact]
    public void Clicking_a_mode_card_changes_the_mode()
    {
        var mode = Run(async ui =>
        {
            ui.Vm.ShowPage("performance");
            await Settle();
            var card = All<Button>(ui.Window).First(b => b.DataContext is ModeItem { Id: "performance" } && b.Classes.Contains("tile"));
            await Click(ui.Window, card);
            await ui.Nitro.RefreshStatus();
            await Settle();
            Assert.Contains("on", card.Classes);
            return ui.Nitro.Status!.Mode;
        });
        Assert.Equal("performance", mode);
    }

    [Fact]
    public void Turbo_button_turns_turbo_on_and_the_header_follows()
    {
        Run(async ui =>
        {
            var turbo = All<Button>(ui.Window).First(b => b.Classes.Contains("turbo"));
            await Click(ui.Window, turbo);
            await ui.Nitro.RefreshStatus();
            await Settle();
            Assert.True(ui.Nitro.Status!.Turbo);
            Assert.Equal("max", ui.Nitro.Status.FanChoice);
            Assert.Equal("Turbo", ui.Vm.ModeText);
            Assert.Contains("on", turbo.Classes);

            await Click(ui.Window, turbo);
            await ui.Nitro.RefreshStatus();
            Assert.False(ui.Nitro.Status!.Turbo);
            Assert.Equal(("balanced", "auto"), (ui.Nitro.Status.Mode, ui.Nitro.Status.FanChoice));
            return true;
        });
    }

    [Fact]
    public void Language_changes_update_the_open_window()
    {
        Run(async ui =>
        {
            Assert.True(HasText(ui.Window, "Controle rápido"));
            ui.Vm.UpdateSettings(s => s with { Language = "en" });
            await Settle();
            Assert.True(HasText(ui.Window, "Quick control"));
            Assert.False(HasText(ui.Window, "Controle rápido"));
            Assert.Equal("Dashboard", ui.Vm.CurrentPage.Title);
            return true;
        });
    }

    [Fact]
    public void Dragging_a_curve_point_edits_the_curve_and_keeps_it_valid()
    {
        Run(async ui =>
        {
            ui.Vm.ShowPage("fans");
            await Settle();
            var editor = All<FanCurveEditor>(ui.Window).Single();
            var before = ui.Vm.Fans.CpuPoints;
            var p = before[2]; // (65 °C, 45 %)
            var w = editor.Bounds.Width - 58;
            var h = editor.Bounds.Height - 44;
            var local = new Point(44 + w * (p.TempC - 20) / 80, 14 + h * (1 - p.Percent / 100.0));
            var start = editor.TranslatePoint(local, ui.Window)!.Value;

            ui.Window.MouseDown(start, MouseButton.Left);
            ui.Window.MouseMove(start + new Point(0, -400)); // far above the next point
            ui.Window.MouseUp(start + new Point(0, -400), MouseButton.Left);
            await Settle();

            var after = ui.Vm.Fans.CpuPoints;
            Assert.Equal(before.Count, after.Count);
            Assert.Equal(p.TempC, after[2].TempC);
            Assert.Equal(after[3].Percent, after[2].Percent); // clamped to the next point: never decreasing
            Assert.True(ui.Vm.Fans.CurveDirty);
            Assert.Null(ui.Vm.Fans.CurveError);

            await ui.Vm.Fans.ApplyCurveCommand.ExecuteAsync(null);
            await ui.Nitro.RefreshStatus();
            Assert.Equal("curve", ui.Nitro.Status!.FanChoice);
            Assert.Equal(after, ui.Nitro.Status.FanCurve!.Cpu);
            return true;
        });
    }

    [Fact]
    public void Every_page_renders_without_binding_errors()
    {
        Run(async ui =>
        {
            foreach (var page in ui.Vm.Pages)
            {
                ui.Vm.CurrentPage = page;
                await Settle();
                Assert.True(HasText(ui.Window, page.Title), page.Key);
                using var frame = ui.Window.CaptureRenderedFrame();
                Assert.NotNull(frame);
            }
            return true;
        });
    }

    [Fact]
    public void Presets_save_apply_and_delete_with_confirmation()
    {
        Run(async ui =>
        {
            ui.Vm.ShowPage("presets");
            await ui.Vm.Presets.Reload();
            Assert.Equal(["bateria", "jogos", "silencioso"], ui.Vm.Presets.Presets.Select(p => p.Name));

            ui.Vm.Presets.NewName = "bad name!";
            Assert.NotNull(ui.Vm.Presets.NameError);
            Assert.False(ui.Vm.Presets.SaveCommand.CanExecute(null));
            ui.Vm.Presets.NewName = "teste";
            await ui.Vm.Presets.SaveCommand.ExecuteAsync(null);
            Assert.Contains("teste", ui.Vm.Presets.Presets.Select(p => p.Name));

            await ui.Vm.Presets.Presets.First(p => p.Name == "jogos").ApplyCommand.ExecuteAsync(null);
            await ui.Nitro.RefreshStatus();
            Assert.True(ui.Nitro.Status!.Turbo);

            // Delete asks first; "no" keeps it
            var delete = ui.Vm.Presets.Presets.First(p => p.Name == "teste").DeleteCommand.ExecuteAsync(null);
            await Settle();
            Assert.NotNull(ui.Vm.Confirm);
            ui.Vm.AnswerCommand.Execute("no");
            await delete;
            Assert.Contains("teste", ui.Vm.Presets.Presets.Select(p => p.Name));

            delete = ui.Vm.Presets.Presets.First(p => p.Name == "teste").DeleteCommand.ExecuteAsync(null);
            await Settle();
            ui.Vm.AnswerCommand.Execute("yes");
            await delete;
            Assert.DoesNotContain("teste", ui.Vm.Presets.Presets.Select(p => p.Name));
            return true;
        });
    }

    [Fact]
    public void Daemon_messages_are_translated()
    {
        Run(ui =>
        {
            Assert.Equal("O turbo só funciona na tomada.", Messages.Translate("turbo needs AC power"));
            Assert.Equal("O modo Desempenho só funciona na tomada.", Messages.Translate("mode performance needs AC power"));
            Assert.StartsWith("Curva insegura", Messages.Translate("unsafe fan curve: two points at 40°C"));
            Assert.Equal("something new", Messages.Translate("something new"));
            ui.Vm.UpdateSettings(s => s with { Language = "en" });
            Assert.Equal("turbo needs AC power", Messages.Translate("turbo needs AC power"));
            return Task.FromResult(true);
        });
    }

    [Fact]
    public void Refused_requests_show_a_translated_notice()
    {
        Run(async ui =>
        {
            ui.Nitro.Demo!.SetPower(onAc: false);
            await ui.Nitro.RefreshStatus();
            await ui.Vm.SetMode("performance");
            await Settle();
            Assert.Contains(ui.Vm.Toasts, t => t.IsError && t.Text == "O modo Desempenho só funciona na tomada.");
            var card = ui.Vm.Performance.Modes.First(m => m.Id == "performance");
            Assert.False(card.IsAvailable);
            return true;
        });
    }
}
