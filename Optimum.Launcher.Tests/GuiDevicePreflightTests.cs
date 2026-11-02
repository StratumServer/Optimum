using System;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Optimum.Launcher.Tests;

public class GuiDevicePreflightTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static readonly GuiDeviceIdentity Hardware = new(
        "Advanced Micro Devices, Inc.", "AMD Radeon RX Test", "4.6 Mesa Test", 4, 6, 1, 0);

    [Fact]
    public void ParseResultAcceptsCompleteMatchingHardwareIdentity()
    {
        Assert.Equal(Hardware, Parse(new GuiDevicePreflightResult(1, Nonce, Hardware)));
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("software-renderer")]
    [InlineData("unsupported-version")]
    [InlineData("unsupported-schema")]
    [InlineData("incomplete-identity")]
    [InlineData("unknown-fields")]
    public void ParseResultRejectsUntrustedOrIncompleteChildOutput(string failure)
    {
        GuiDevicePreflightResult result = failure switch
        {
            "wrong-nonce" => new(1, "wrong", Hardware),
            "software-renderer" => new(1, Nonce, Hardware with { Renderer = "llvmpipe (LLVM 20)" }),
            "unsupported-version" => new(1, Nonce, Hardware with { Major = 4, Minor = 2 }),
            "unsupported-schema" => new(2, Nonce, Hardware),
            "incomplete-identity" => new(1, Nonce, Hardware with { Vendor = "unknown" }),
            "unknown-fields" => new(1, Nonce, Hardware),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        byte[] bytes = Serialize(result);
        if (failure == "unknown-fields")
        {
            string json = Encoding.UTF8.GetString(bytes);
            bytes = Encoding.UTF8.GetBytes(json[..1] + "\"unexpected\":true," + json[1..]);
        }

        Assert.Null(GuiDevicePreflight.ParseResult(bytes, Nonce, 4, 3));
    }

    [Fact]
    public void ParseResultRejectsOversizedMalformedAndDuplicateJson()
    {
        Assert.Null(GuiDevicePreflight.ParseResult([], Nonce, 4, 3));
        Assert.Null(GuiDevicePreflight.ParseResult(Encoding.UTF8.GetBytes("{"), Nonce, 4, 3));
        Assert.Null(GuiDevicePreflight.ParseResult(new byte[16 * 1024 + 1], Nonce, 4, 3));
        Assert.Null(GuiDevicePreflight.ParseResult(
            Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"SchemaVersion\":1}"), Nonce, 4, 3));
    }

    private static GuiDeviceIdentity? Parse(GuiDevicePreflightResult result) =>
        GuiDevicePreflight.ParseResult(Serialize(result), Nonce, 4, 3);

    private static byte[] Serialize(GuiDevicePreflightResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result);
}
