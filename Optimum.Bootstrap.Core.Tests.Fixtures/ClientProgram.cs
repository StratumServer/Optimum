namespace Vintagestory.Client
{
    public static class ClientProgram
    {
        public static void Main() { }
    }
}

namespace Optimum.Patcher
{
    public static class Program
    {
        public static bool OmitDigest;

        public static int Run(string[] args) => Run(args, out _);

        public static int Run(string[] args, out string? patchedSha256)
        {
            string target = args[^1];
            System.IO.File.WriteAllText(target, "patched\n");
            if (OmitDigest)
            {
                patchedSha256 = null;
                return 0;
            }

            byte[] bytes = System.IO.File.ReadAllBytes(target);
            patchedSha256 = "sha256:" + System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            return 0;
        }
    }
}
