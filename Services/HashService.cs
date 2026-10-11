using System.Security.Cryptography;

namespace Valve_Diff_Tool.Services
{
    public sealed class HashService
    {
        public string ComputeHash(string filePath)
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        public bool AreIdentical(string first, string second)
        {
            return string.Equals(
                ComputeHash(first),
                ComputeHash(second),
                StringComparison.Ordinal);
        }
    }
}
