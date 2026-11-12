namespace Vintagestory.API.Config;

internal static class OptimumChunkUploadPriority
{
    internal static int ApplyAge(
        int priority,
        long readyTimestamp,
        long sortTimestamp,
        double priorityUnitsPerTick,
        bool inCurrentFrustum)
    {
        if (!inCurrentFrustum || readyTimestamp <= 0 || sortTimestamp <= readyTimestamp || priorityUnitsPerTick <= 0)
        {
            return priority;
        }

        double ageCredit = (sortTimestamp - readyTimestamp) * priorityUnitsPerTick;
        if (ageCredit < 1)
        {
            return priority;
        }

        long creditToMinimumPriority = (long)priority - int.MinValue;
        if (ageCredit >= creditToMinimumPriority)
        {
            return int.MinValue;
        }

        return (int)((long)priority - (long)ageCredit);
    }
}
