using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Bootstrap.Core.Platform;
using Vintagestory.Client;
using Xunit;

namespace Optimum.Bootstrap.Core.Tests
{
    public sealed class RuntimeValidatorTests
    {
        [Fact]
        public void ValidateRequiresGameContentAssembly()
        {
            string packageDirectory = CreatePackageFixture();
            try
            {
                var validator = new RuntimeValidator(SystemProbe.Default);
                RuntimeValidationResult complete = validator.Validate(packageDirectory);
                Assert.True(complete.Ok, complete.Detail);

                File.Delete(Path.Combine(packageDirectory, "Optimum.GameContent.dll"));

                RuntimeValidationResult missing = validator.Validate(packageDirectory);
                Assert.False(missing.Ok);
                Assert.Equal("missing assembly: Optimum.GameContent.dll", missing.Detail);
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        [Fact]
        public void ValidateRejectsMissingReferenceFromPatchedAssembly()
        {
            string packageDirectory = CreatePackageFixture();
            try
            {
                File.Delete(Path.Combine(packageDirectory, "Optimum.Bootstrap.Core.dll"));

                RuntimeValidationResult result = new RuntimeValidator(SystemProbe.Default).Validate(packageDirectory);

                Assert.False(result.Ok);
                Assert.Contains("references missing assembly: Optimum.Bootstrap.Core", result.Detail ?? string.Empty);
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        private static string CreatePackageFixture()
        {
            string packageDirectory = Path.Combine(Path.GetTempPath(), $"optimum-runtime-validator-{Guid.NewGuid():N}");
            string modsDirectory = Path.Combine(packageDirectory, "Mods");
            string optimumDirectory = Path.Combine(packageDirectory, ".optimum");
            Directory.CreateDirectory(modsDirectory);
            Directory.CreateDirectory(optimumDirectory);
            File.WriteAllText(Path.Combine(packageDirectory, "Optimum.exe"), string.Empty);

            string testAssembly = typeof(RuntimeValidatorTests).Assembly.Location;
            string clientAssembly = typeof(ClientProgram).Assembly.Location;
            foreach (string name in new[]
            {
                "VintagestoryLib.dll",
                "VintagestoryAPI.dll",
                "Vintagestory.dll",
                "Optimum.GameContent.dll",
                "Mods/VSEssentials.dll",
                "Mods/VSSurvivalMod.dll",
            })
            {
                string path = Path.Combine(packageDirectory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(name == "VintagestoryLib.dll" ? clientAssembly : testAssembly, path);
            }

            foreach (string dependency in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
                File.Copy(dependency, Path.Combine(packageDirectory, Path.GetFileName(dependency)), overwrite: true);

            var manifest = new PatchInstallManifest
            {
                OptimumVersion = "test",
                PatchedAtUtc = DateTimeOffset.UtcNow,
                GameDirectory = packageDirectory,
                Targets = [new PatchTargetRecord { Assembly = "Mods/VSEssentials.dll" }],
            };
            File.WriteAllText(Path.Combine(optimumDirectory, "manifest.json"), manifest.Serialize());
            return packageDirectory;
        }
    }
}
