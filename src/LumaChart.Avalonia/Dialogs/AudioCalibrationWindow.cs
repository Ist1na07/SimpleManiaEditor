using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;
using System.Globalization;

namespace LumaChart.Avalonia.Dialogs;

/// <summary>Audible clicks and visual crossings use the same editor output clock.</summary>
public sealed class AudioCalibrationWindow : Window
{
    private readonly WindowsAudioPlaybackService audio = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly Slider slider = new() { Minimum = -500, Maximum = 500, SmallChange = 1, LargeChange = 10 };
    private readonly TextBlock value = new();
    private readonly TextBlock status = new();
    private readonly CalibrationPulse pulse;
    private string? path;
    private bool closing;
    private bool busy;
    private Task fixtureWriteTask = Task.CompletedTask;
    public Task Shutdown { get; private set; } = Task.CompletedTask;
    public int CalibrationMilliseconds => (int)Math.Round(slider.Value);

    public AudioCalibrationWindow(int initial)
    {
        Title = "编辑器音画校准器"; Width = 640; Height = 480; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#191E20"));
        slider.Value = initial;
        pulse = new CalibrationPulse(() => audio.Position.TotalMilliseconds + CalibrationMilliseconds);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var delta in new[] { -10, -1, 1, 10 })
        {
            var step = new Button { Content = delta.ToString("+0;-0", CultureInfo.InvariantCulture) + " ms" };
            step.Click += (_, _) => slider.Value = Math.Clamp(slider.Value + delta, -500, 500); controls.Children.Add(step);
        }
        var test = new Button { Content = "开始 / 暂停试听（Space）" };
        test.Click += async (_, _) => await ToggleAsync();
        var apply = new Button { Content = "使用此校准值", HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) => Close((int?)CalibrationMilliseconds);
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Click += (_, _) => Close((int?)null);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        actions.Children.Add(apply); Grid.SetColumn(cancel, 1); actions.Children.Add(cancel);
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            new TextBlock { Text = "让声音与光点穿过中心线的瞬间重合", FontSize = 17, Foreground = Brushes.White },
            new TextBlock { Text = "每 1200 ms 发出一次短音。先粗调，再用 ±1 ms 微调，反复试听确认。正值让画面提前，负值让画面延后。以眼耳对齐为准，避免点拍反应时间带来偏差。", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.LightGray },
            pulse, value, slider, controls, test, status, actions
        } };
        slider.ValueChanged += (_, _) => { UpdateValue(); pulse.InvalidateVisual(); };
        UpdateValue();
        timer.Tick += async (_, _) =>
        {
            pulse.InvalidateVisual();
            if (!busy && !closing && audio.IsPlaying && audio.Position.TotalSeconds > 118) { busy = true; try { await audio.SeekAsync(TimeSpan.FromMilliseconds(500)); } catch (Exception ex) { if (!closing) status.Text = ex.Message; } finally { busy = false; } }
        };
        AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Space) { e.Handled = true; await ToggleAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close((int?)null); }
        }, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, (_, e) => { if (e.Key == Key.Space) e.Handled = true; }, RoutingStrategies.Tunnel);
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => { closing = true; timer.Stop(); Shutdown = ShutdownAsync(); };
    }

    private async Task ShutdownAsync()
    {
        try { await audio.DisposeAsync(); await fixtureWriteTask; }
        finally { if (path != null) try { File.Delete(path); } catch (IOException) { } }
    }

    private void UpdateValue() => value.Text = $"当前校准：{CalibrationMilliseconds:+0;-0;0} ms";
    private async Task ToggleAsync()
    {
        if (busy || closing) return; busy = true;
        try
        {
            if (!audio.IsLoaded)
            {
                path = Path.Combine(Path.GetTempPath(), $"sme-calibration-{Guid.NewGuid():N}.wav");
                fixtureWriteTask = Task.Run(() => WriteClickTrack(path));
                await fixtureWriteTask;
                if (closing) return;
                await audio.LoadAsync(path); await audio.SetVolumeAsync(.5f);
            }
            if (closing) return;
            if (audio.IsPlaying) await audio.PauseAsync(); else await audio.PlayAsync();
            status.Text = audio.IsPlaying ? "试听中 · 不满意可继续微调" : "已暂停";
        }
        catch (Exception ex) { if (!closing) status.Text = $"试听失败：{ex.Message}"; }
        finally { busy = false; }
    }

    public static void WriteClickTrack(string file)
    {
        const int rate = 48000, frames = rate * 120, period = rate * 6 / 5;
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(frames * 2);
        for (int frame = 0; frame < frames; frame++)
        {
            var phase = frame % period;
            var sample = frame < period || phase >= rate / 100 ? 0 : .7 * Math.Sin(2 * Math.PI * 1500 * phase / rate) * Math.Exp(-phase / 110d);
            writer.Write((short)(sample * short.MaxValue));
        }
    }

    private sealed class CalibrationPulse(Func<double> position) : Control
    {
        public CalibrationPulse() : this(() => 0) { }
        protected override Size MeasureOverride(Size availableSize) => new(availableSize.Width, 90);
        public override void Render(DrawingContext context)
        {
            var phase = ((position() + 600) % 1200 + 1200) % 1200 - 600;
            var center = Bounds.Width / 2;
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#0F1416")), null, new Rect(Bounds.Size));
            context.DrawLine(new Pen(Brushes.Gray, 1), new Point(20, 45), new Point(Bounds.Width - 20, 45));
            context.DrawLine(new Pen(Math.Abs(phase) < 25 ? Brushes.White : Brushes.Gray, 2), new Point(center, 15), new Point(center, 75));
            context.DrawEllipse(Brushes.LightCyan, null, new Point(center + phase / 600 * (center - 24), 45), 7, 7);
        }
    }
}
