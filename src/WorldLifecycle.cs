namespace FakeCheats;

// Plugin loading can happen before CS2 has created its global variables.
// Player enumeration is allowed only after a world-update callback, and a
// callback queued for an old map/plugin lifetime must never reopen that gate.
public sealed class WorldLifecycle
{
    private int _generation;
    public bool IsLoaded { get; private set; }
    public bool IsReady { get; private set; }

    public void Load()
    {
        IsLoaded = true;
        Suspend();
    }

    public void Suspend()
    {
        IsReady = false;
        _generation++;
    }

    public void Unload()
    {
        IsLoaded = false;
        Suspend();
    }

    public Action OnNextWorldUpdate(Action initialize)
    {
        var generation = _generation;
        return () =>
        {
            if (!IsLoaded || generation != _generation) return;
            IsReady = true;
            initialize();
        };
    }
}
