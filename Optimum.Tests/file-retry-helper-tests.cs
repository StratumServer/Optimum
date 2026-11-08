using System;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Launcher;
using Xunit;

namespace Optimum.Tests;

public sealed class FileRetryHelperTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020), true)] // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true)] // ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070002), false)] // ERROR_FILE_NOT_FOUND
    [InlineData(unchecked((int)0x80070005), false)] // ERROR_ACCESS_DENIED
    public void IsSharingOrLockViolation_IdentifiesExpectedHResults(int hresult, bool expected)
    {
        var ex = Marshal.GetExceptionForHR(hresult)!;
        Assert.Equal(expected, FileRetryHelper.IsSharingOrLockViolation(ex));
    }

    [Fact]
    public void Retry_RecoversFromTransientSharingViolation()
    {
        int attempts = 0;
        FileRetryHelper.Retry(() =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw (IOException)Marshal.GetExceptionForHR(unchecked((int)0x80070020))!;
            }
        }, maxAttempts: 5, initialDelayMs: 1);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Retry_ThrowsWhenMaxAttemptsExceeded()
    {
        int attempts = 0;
        Assert.ThrowsAny<IOException>(() =>
        {
            FileRetryHelper.Retry(() =>
            {
                attempts++;
                throw (IOException)Marshal.GetExceptionForHR(unchecked((int)0x80070020))!;
            }, maxAttempts: 3, initialDelayMs: 1);
        });

        Assert.Equal(3, attempts);
    }
}
