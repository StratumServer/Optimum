using System;
using Optimum.Render.Vulkan.Shaders;

namespace Optimum.Shaders.Compiler;

internal static class Program
{
    private static int Main(string[] args) => NativeShaderTool.Run(args, Console.Out, Console.Error);
}
