using FakeCheats;

internal static class DeferredConVarSyncTests
{
    public static void Run(Action<bool, string> check)
    {
        var sync = new DeferredConVarSync();
        var queued = new Queue<Action>();
        var inNativeCallback = false;
        var realCheats = false;
        var writes = 0;
        var broadcasts = 0;
        var fakeCheats = 1;
        var sentValue = -1;

        void OnChanged() => sync.Request(queued.Enqueue, Synchronize);
        void SetRealCheats(bool value)
        {
            if (inNativeCallback) throw new InvalidOperationException("Reentrant native ConVar write.");
            realCheats = value;
            writes++;
            inNativeCallback = true;
            try { OnChanged(); }
            finally { inNativeCallback = false; }
        }
        void Synchronize()
        {
            if (realCheats) SetRealCheats(false);
            broadcasts++;
            sentValue = fakeCheats;
        }

        OnChanged();
        check(queued.Count == 0, "ConVar notifications before load do not schedule engine work");
        sync.Start();
        SetRealCheats(true);
        check(realCheats && writes == 1 && broadcasts == 0 && queued.Count == 1,
            "Enabling real cheats returns from the native callback without a nested write or broadcast");
        for (var i = 0; i < 10; i++) OnChanged();
        check(queued.Count == 1, "Repeated ConVar notifications share one pending synchronization");
        fakeCheats = 0;
        queued.Dequeue()();
        check(!realCheats && writes == 2 && broadcasts == 1 && sentValue == 0,
            "Deferred correction restores real cheats and uses the latest visual switch");
        check(queued.Count == 0, "The correction's own native notification does not schedule another correction");

        SetRealCheats(true);
        var beforeUnload = queued.Dequeue();
        sync.Cancel();
        beforeUnload();
        OnChanged();
        check(realCheats && writes == 3 && broadcasts == 1 && queued.Count == 0,
            "Unload cancels queued corrections and ignores later notifications");

        sync.Start();
        OnChanged();
        var oldMap = queued.Dequeue();
        sync.Cancel();
        sync.Start();
        OnChanged();
        oldMap();
        OnChanged();
        check(queued.Count == 1 && writes == 3 && broadcasts == 1,
            "A previous map's callback cannot run or clear the current map's pending synchronization");
        queued.Dequeue()();
        check(!realCheats && broadcasts == 2, "A new map resumes deferred ConVar correction");

        sync.Request(queued.Enqueue, () => throw new InvalidOperationException("Test sync failure"));
        var syncFailed = false;
        try { queued.Dequeue()(); }
        catch (InvalidOperationException) { syncFailed = true; }
        OnChanged();
        check(syncFailed && queued.Count == 1, "A failed synchronization releases the running guard");
        queued.Dequeue()();

        var scheduleFailed = false;
        try { sync.Request(_ => throw new InvalidOperationException("Test schedule failure"), Synchronize); }
        catch (InvalidOperationException) { scheduleFailed = true; }
        OnChanged();
        check(scheduleFailed && queued.Count == 1, "A scheduling failure releases the pending guard");
        queued.Dequeue()();
        check(!realCheats && broadcasts == 4, "Synchronization recovers after callback and scheduler failures");
        sync.Cancel();
    }
}
