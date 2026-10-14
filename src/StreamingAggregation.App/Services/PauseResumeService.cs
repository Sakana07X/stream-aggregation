namespace StreamingAggregation.App.Services;

/// <summary>
/// 暂停/恢复处理服务。管理消息聚合管线的暂停状态。
/// </summary>
public sealed class PauseResumeService
{
    private bool _isPaused;
    private readonly object _gate = new();

    /// <summary>
    /// 当前是否处于暂停状态。
    /// </summary>
    public bool IsPaused
    {
        get { lock (_gate) return _isPaused; }
    }

    /// <summary>
    /// 暂停状态变更事件。
    /// </summary>
    public event EventHandler<bool>? PauseStateChanged;

    /// <summary>
    /// 暂停消息处理。
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_isPaused) return;
            _isPaused = true;
        }
        PauseStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// 恢复消息处理。
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_isPaused) return;
            _isPaused = false;
        }
        PauseStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// 切换暂停/恢复状态。
    /// </summary>
    public void Toggle()
    {
        if (IsPaused)
            Resume();
        else
            Pause();
    }
}
