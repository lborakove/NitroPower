using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
public class UiTests(HeadlessFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<HeadlessFixture>
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
        control.BringIntoView();
        await Settle();
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
    public void Language_combo_switches_back_and_forth()
    {
        var auto = Loc.FromEnvironment();
        var seen = Run(async ui =>
        {
            ui.Vm.ShowPage("system");
            await Settle();
            var seen = new List<string>();
            foreach (var choice in new[] { "en", "pt", "en", "auto", "pt", "en" })
            {
                var combo = All<ComboBox>(ui.Window).First(c => c.ItemsSource is IEnumerable<Choice> l && l.Any(i => i.Value == "pt"));
                combo.SelectedIndex = ((IEnumerable<Choice>)combo.ItemsSource!).ToList().FindIndex(c => c.Value == choice);
                await Settle();
                seen.Add($"{ui.Vm.Settings.Language} {Loc.Instance.Language} {(combo.SelectedItem as Choice)?.Value}");
            }
            return seen;
        });
        Assert.Equal(["en en en", "pt pt pt", "en en en", $"auto {auto} auto", "pt pt pt", "en en en"], seen);
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

    private static string Describe(Control c) =>
        c is Button { CommandParameter: string p }
            ? p
            : All<TextBlock>(c).Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? c.GetType().Name;

    /// <summary>Visible option buttons ("seg" bars and "tile" cards), one list per bar or list.</summary>
    private static List<(string Sig, List<Button> Buttons)> ChoiceGroups(Visual root) =>
        All<Button>(root)
            .Where(b => b.IsEffectivelyVisible && (b.Classes.Contains("seg") || b.Classes.Contains("tile")))
            .GroupBy(b => b.GetVisualAncestors().FirstOrDefault(v => v is ItemsControl || (v is Border br && br.Classes.Contains("segbar")))
                          ?? b.GetVisualParent())
            .Select(g => g.ToList())
            .Select(g => (string.Join("|", g.Select(Describe)), g))
            .ToList();

    private static async Task AnswerYesIfAsked(Ui ui)
    {
        if (ui.Vm.Confirm is null)
            return;
        var yes = All<Button>(ui.Window).First(b => b.IsEffectivelyVisible && b.Command == ui.Vm.AnswerCommand && "yes".Equals(b.CommandParameter));
        await Click(ui.Window, yes);
    }

    private static async Task Refresh(Ui ui)
    {
        await AnswerYesIfAsked(ui);
        await ui.Nitro.RefreshStatus();
        await Settle();
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("performance")]
    [InlineData("fans")]
    [InlineData("battery")]
    [InlineData("device")]
    [InlineData("nitrokey")]
    public void Every_option_button_stays_selected_after_a_click(string page)
    {
        var (clicks, problems) = Run(async ui =>
        {
            var problems = new List<string>();
            var tested = new HashSet<string>();
            var clicks = 0;
            if (page == "nitrokey")
            {
                ui.Nitro.Demo!.SimulateDriverModeKey(); // shows the mode key options
                await ui.Nitro.RefreshStatus();
            }
            while (true)
            {
                ui.Vm.ShowPage(page);
                await Settle();
                var next = ChoiceGroups(ui.Window).FirstOrDefault(g => !tested.Contains(g.Sig));
                if (next.Buttons is null)
                    break;
                tested.Add(next.Sig);
                for (var i = 0; i < next.Buttons.Count; i++)
                {
                    ui.Vm.ShowPage(page);
                    await Settle();
                    var group = ChoiceGroups(ui.Window).FirstOrDefault(g => g.Sig == next.Sig).Buttons;
                    if (group is null)
                    {
                        problems.Add($"[{next.Sig}] disappeared");
                        break;
                    }
                    var name = Describe(group[i]);
                    if (!group[i].IsEffectivelyEnabled)
                    {
                        problems.Add($"[{next.Sig}] {name} is disabled");
                        continue;
                    }
                    await Click(ui.Window, group[i]);
                    await Refresh(ui);
                    clicks++;
                    if (ui.Vm.CurrentPage.Key != page)
                    {
                        // Manual fan speeds need the sliders: the dashboard opens the Fans page for them
                        if (!(page == "dashboard" && name == "manual" && ui.Vm.CurrentPage.Key == "fans" && ui.Vm.Fans.IsManual))
                            problems.Add($"[{next.Sig}] {name} opened {ui.Vm.CurrentPage.Key}");
                        continue;
                    }
                    var after = ChoiceGroups(ui.Window).FirstOrDefault(g => g.Sig == next.Sig).Buttons;
                    var lit = after?.Where(b => b.Classes.Contains("on")).Select(Describe).ToList() ?? [];
                    if (lit.Count != 1 || lit[0] != name)
                        problems.Add($"[{next.Sig}] clicked {name}, lit: [{string.Join(", ", lit)}]");
                }
            }
            return (clicks, problems);
        });
        output.WriteLine($"{page}: {clicks} option clicks");
        Assert.Empty(problems);
        Assert.True(clicks >= 2, $"only {clicks} option buttons found on {page}");
    }

    [Theory]
    [InlineData("performance")]
    [InlineData("fans")]
    [InlineData("battery")]
    [InlineData("device")]
    [InlineData("system")]
    public void Every_switch_and_checkbox_toggles_and_holds(string page)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var problems = Run(async ui =>
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ui.Dir);
            var problems = new List<string>();
            ui.Vm.ShowPage(page);
            if (page == "fans")
                await ui.Vm.Fans.SelectCommand.ExecuteAsync("manual"); // shows "link speeds"
            await Settle();
            var count = All<ToggleButton>(ui.Window).Count(t => t.IsEffectivelyVisible);
            if (count == 0)
                problems.Add("no switches found");
            var toggled = 0;
            for (var i = 0; i < count; i++)
            {
                var toggle = All<ToggleButton>(ui.Window).Where(t => t.IsEffectivelyVisible).ElementAt(i);
                var name = Describe((Control?)toggle.GetVisualAncestors().OfType<Grid>().FirstOrDefault() ?? toggle);
                if (!toggle.IsEffectivelyEnabled)
                    continue;
                var before = toggle.IsChecked == true;
                toggled++;
                foreach (var want in new[] { !before, before })
                {
                    await Click(ui.Window, toggle);
                    await Refresh(ui);
                    toggle = All<ToggleButton>(ui.Window).Where(t => t.IsEffectivelyVisible).ElementAt(i);
                    if ((toggle.IsChecked == true) != want)
                        problems.Add($"{name}: wanted {(want ? "on" : "off")}, shows {(toggle.IsChecked == true ? "on" : "off")}");
                }
            }
            output.WriteLine($"{page}: {toggled} of {count} switches toggled");
            return problems;
        });
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("device")]
    public void Every_combo_item_can_be_chosen_in_both_languages(string page)
    {
        var problems = Run(async ui =>
        {
            var problems = new List<string>();
            foreach (var language in new[] { "en", "pt" })
            {
                ui.Vm.UpdateSettings(s => s with { Language = language });
                ui.Vm.ShowPage(page);
                await Settle();
                var combos = All<ComboBox>(ui.Window).Where(c => c.IsEffectivelyVisible).ToList();
                if (combos.Count == 0)
                    problems.Add("no combo boxes found");
                for (var c = 0; c < combos.Count; c++)
                {
                    var items = combos[c].ItemCount;
                    for (var i = 0; i < items; i++)
                    {
                        var combo = All<ComboBox>(ui.Window).Where(x => x.IsEffectivelyVisible).ElementAt(c);
                        combo.SelectedIndex = i;
                        await Refresh(ui);
                        combo = All<ComboBox>(ui.Window).Where(x => x.IsEffectivelyVisible).ElementAt(c);
                        if (combo.SelectedIndex != i)
                            problems.Add($"{language} combo {c}: chose item {i}, shows {combo.SelectedIndex}");
                    }
                }
                // Leave the language as it was chosen in this round, whatever the last combo item did
                ui.Vm.UpdateSettings(s => s with { Language = language });
                await Settle();
            }
            return problems;
        });
        Assert.Empty(problems);
    }

    [Fact]
    public void Effect_choice_survives_a_language_change()
    {
        var (index, name) = Run(async ui =>
        {
            ui.Vm.ShowPage("device");
            await Settle();
            var combo = All<ComboBox>(ui.Window).First(c => c.ItemsSource == ui.Vm.Device.Effects);
            combo.SelectedIndex = 2;
            await Refresh(ui);
            ui.Vm.UpdateSettings(s => s with { Language = "en" });
            await Settle();
            return (combo.SelectedIndex, ui.Vm.Device.SelectedEffect.Name);
        });
        Assert.Equal(2, index);
        Assert.Equal(Strings.Get("rgb.effect." + Enum.GetValues<Core.Hardware.KeyboardEffect>()[2].ToString().ToLowerInvariant(), "en"), name);
    }

    [Fact]
    public void Navigation_list_selects_every_page_and_keeps_it_after_a_language_change()
    {
        var problems = Run(async ui =>
        {
            var problems = new List<string>();
            var nav = All<ListBox>(ui.Window).First(l => l.Classes.Contains("nav"));
            for (var i = 0; i < ui.Vm.Pages.Count; i++)
            {
                var item = All<ListBoxItem>(nav).ElementAt(i);
                await Click(ui.Window, item);
                if (ui.Vm.CurrentPage != ui.Vm.Pages[i])
                    problems.Add($"clicked {ui.Vm.Pages[i].Key}, current {ui.Vm.CurrentPage.Key}");
            }
            ui.Vm.UpdateSettings(s => s with { Language = "en" });
            await Settle();
            if (nav.SelectedItem != ui.Vm.CurrentPage)
                problems.Add("navigation selection lost after language change");
            return problems;
        });
        Assert.Empty(problems);
    }
}
