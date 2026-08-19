using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Allows one worker to own a request at a time.
/// </summary>
public sealed class OptimumDispatchClaim
{
    private int _claimed;

    public bool TryClaim()
    {
        return Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    public void Release()
    {
        Volatile.Write(ref _claimed, 0);
    }
}
