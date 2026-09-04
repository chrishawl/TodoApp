using System.Security.Cryptography;
using System.Text;

namespace TodoApp.Eval;

internal static class EvaluationAssetFingerprint
{
    public static string PrivateTests(string controllerRoot)
    {
        var root = Path.Combine(controllerRoot, "private-tests");
        if (!Directory.Exists(root)) throw new HarnessException($"Private-test assets are missing: {root}");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!IsSourceAsset(relative)) continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsSourceAsset(string path) => !path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => string.Equals(segment, "bin", StringComparison.Ordinal) ||
                        string.Equals(segment, "obj", StringComparison.Ordinal));
}
