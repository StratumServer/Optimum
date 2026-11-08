using System;
using System.IO;
using System.Threading;

namespace Optimum.Launcher;

public static class FileRetryHelper
{
    public static void Retry(Action action, int maxAttempts = 10, int initialDelayMs = 100)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts && IsSharingOrLockViolation(ex))
            {
                Thread.Sleep(initialDelayMs * attempt);
            }
        }
    }

    public static bool IsSharingOrLockViolation(Exception ex)
    {
        // 0x80070020 = ERROR_SHARING_VIOLATION, 0x80070021 = ERROR_LOCK_VIOLATION
        int hr = ex.HResult;
        return hr == unchecked((int)0x80070020) || hr == unchecked((int)0x80070021);
    }
}
