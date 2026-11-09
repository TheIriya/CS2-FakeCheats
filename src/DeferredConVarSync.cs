namespace FakeCheats;

// Native ConVar callbacks must return before another value is written. Coalesce
// notifications and discard queued work after map changes or plugin unload.
public sealed class DeferredConVarSync
{
    private int _generation;
    private bool _active;
    private bool _pending;
    private bool _running;

    public void Start()
    {
        Cancel();
        _active = true;
    }

    public void Cancel()
    {
        _active = false;
        _pending = false;
        _generation++;
    }

    public void Request(Action<Action> schedule, Action synchronize)
    {
        if (!_active || _pending || _running) return;
        var generation = _generation;
        _pending = true;
        try
        {
            schedule(() =>
            {
                if (!_active || generation != _generation) return;
                _pending = false;
                _running = true;
                try
                {
                    synchronize();
                }
                finally
                {
                    _running = false;
                }
            });
        }
        catch
        {
            if (generation == _generation) _pending = false;
            throw;
        }
    }
}
