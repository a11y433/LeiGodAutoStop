using System.Diagnostics;

namespace LeiGodAutoStop;

public sealed class PauseOnlyMonitor(TimeSpan? idle = null)
{
    private readonly TimeSpan _idle = idle ?? TimeSpan.FromSeconds(120);
    private string? _key;
    private string? _completedKey;
    private long? _startedAt;

    public string State { get; private set; } = "等待雷神";
    public TimeSpan Remaining { get; private set; } = idle ?? TimeSpan.FromSeconds(120);
    public string? CurrentKey => _key;

    public bool Tick(string? safeIdleKey, string reason, long now)
    {
        if (string.IsNullOrWhiteSpace(safeIdleKey))
        {
            _key = null;
            _startedAt = null;
            _completedKey = null;
            State = reason;
            Remaining = _idle;
            return false;
        }

        if (!string.Equals(_key, safeIdleKey, StringComparison.Ordinal))
        {
            _key = safeIdleKey;
            _startedAt = now;
            _completedKey = null;
        }

        if (string.Equals(_completedKey, safeIdleKey, StringComparison.Ordinal))
        {
            State = "本轮空闲已暂停";
            Remaining = TimeSpan.Zero;
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(_startedAt!.Value, now);
        Remaining = elapsed >= _idle ? TimeSpan.Zero : _idle - elapsed;
        State = elapsed >= _idle ? "达到暂停阈值" : "无加速游戏空闲计时中";
        return elapsed >= _idle;
    }

    public void MarkPaused(string key)
    {
        if (!string.Equals(_key, key, StringComparison.Ordinal)) return;
        _completedKey = key;
        State = "本轮空闲已暂停";
        Remaining = TimeSpan.Zero;
    }

    public void RetryAfter(string key, long now)
    {
        if (!string.Equals(_key, key, StringComparison.Ordinal)) return;
        _startedAt = now;
        State = "暂停未验证；重新观察120秒";
        Remaining = _idle;
    }

    public void Reset(string reason = "等待雷神")
    {
        _key = null;
        _startedAt = null;
        _completedKey = null;
        State = reason;
        Remaining = _idle;
    }
}
