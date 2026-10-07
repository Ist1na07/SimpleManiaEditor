using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.ViewModels;

/// <summary>Active-tab, source-revision-aware owner for waveform/spectrum generation.</summary>
public sealed class WindowsAudioVisualizationViewModel : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<string, CancellationToken, Task<AudioVisualization>> loader;
    private readonly Func<string, string?> revisionResolver;
    private CancellationTokenSource cancellation = new();
    private Guid? activeTabId;
    private string? requestedPath;
    private string? requestedRevision;
    private AudioVisualization? visualization;
    private string status = "没有打开的谱面";
    private Exception? lastError;
    private long generation;
    private Task pendingLoad = Task.CompletedTask;
    private int disposed;

    public WindowsAudioVisualizationViewModel()
        : this(AudioWaveformService.LoadVisualizationAsync, ResolveRevision)
    {
    }

    internal WindowsAudioVisualizationViewModel(
        Func<string, CancellationToken, Task<AudioVisualization>> loader,
        Func<string, string?> revisionResolver)
    {
        this.loader = loader;
        this.revisionResolver = revisionResolver;
    }

    public event EventHandler? StateChanged;

    public Guid? ActiveTabId { get { lock (gate) return activeTabId; } }
    public AudioVisualization? Visualization { get { lock (gate) return visualization; } }
    public string Status { get { lock (gate) return status; } }
    public Exception? LastError { get { lock (gate) return lastError; } }
    internal Task PendingLoad { get { lock (gate) return pendingLoad; } }

    public void Activate(Guid? tabId, string? audioPath, string? audioReference = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var revision = audioPath == null ? null : revisionResolver(audioPath);
        CancellationToken token;
        long requestGeneration;
        lock (gate)
        {
            if (activeTabId == tabId &&
                string.Equals(requestedPath, audioPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(requestedRevision, revision, StringComparison.Ordinal)) return;
            activeTabId = tabId;
            requestedPath = audioPath;
            requestedRevision = revision;
            visualization = null;
            lastError = null;
            requestGeneration = ++generation;
            cancellation.Cancel();
            cancellation.Dispose();
            cancellation = new CancellationTokenSource();
            token = cancellation.Token;
            status = tabId == null
                ? "没有打开的谱面"
                : string.IsNullOrWhiteSpace(audioReference)
                    ? "当前谱面没有引用音频可视化"
                    : audioPath == null || revision == null
                        ? AvaloniaLocalizationCatalog.Format("未找到可视化音频：{0}", audioReference)
                        : AvaloniaLocalizationCatalog.Format("正在生成波形与频谱：{0}", Path.GetFileName(audioPath));
        }
        RaiseStateChanged();
        if (audioPath == null || revision == null) return;
        var task = LoadAsync(tabId!.Value, audioPath, revision, requestGeneration, token);
        lock (gate) pendingLoad = task;
    }

    private async Task LoadAsync(
        Guid tabId,
        string path,
        string revision,
        long requestGeneration,
        CancellationToken token)
    {
        try
        {
            var result = await loader(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(revisionResolver(path), revision, StringComparison.Ordinal))
                throw new IOException("音频源在可视化生成期间发生了变化。");
            lock (gate)
            {
                if (requestGeneration != generation || activeTabId != tabId ||
                    !string.Equals(requestedRevision, revision, StringComparison.Ordinal)) return;
                visualization = result;
                status = result.Waveform == null && result.Spectrogram == null
                    ? "音频可视化为空"
                    : AvaloniaLocalizationCatalog.Format("波形与频谱已生成：{0}", Path.GetFileName(path));
                lastError = null;
            }
            RaiseStateChanged();
        }
        catch (OperationCanceledException)
        {
            // Tab/source replacement is an expected latest-wins transition.
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (requestGeneration != generation || activeTabId != tabId) return;
                visualization = null;
                lastError = exception;
                status = AvaloniaLocalizationCatalog.Format("波形 / 频谱生成失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            }
            RaiseStateChanged();
        }
    }

    private static string? ResolveRevision(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Task pending;
        lock (gate)
        {
            generation++;
            cancellation.Cancel();
            pending = pendingLoad;
            visualization = null;
            status = "音频可视化服务已释放";
        }
        try { await pending.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }
}
