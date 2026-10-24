using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LeiGodAutoStop;

public static class PackageIntegrity
{
    private static readonly Regex ManifestLine = new(@"^([A-Fa-f0-9]{64})\s+LeiGodAutoStop\.exe$", RegexOptions.CultureInvariant);

    public static bool Verify(out string detail)
    {
        try
        {
            var exe = Path.GetFullPath(Application.ExecutablePath);
            var folder = Path.GetDirectoryName(exe)!;
            var manifestPath = Path.Combine(folder, "SHA256.txt");
            if (!File.Exists(manifestPath)) { detail = "找不到随包 SHA256.txt；请从完整 ZIP 解压后运行。"; return false; }
            var line = File.ReadAllText(manifestPath).Trim('\uFEFF', '\u0000', ' ', '\r', '\n', '\t');
            var match = ManifestLine.Match(line);
            if (!match.Success) { detail = "SHA256.txt 格式无效。"; return false; }
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            var expected = match.Groups[1].Value;
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
            { detail = $"程序文件校验失败。期望 {expected}，实际 {actual}。请重新解压完整安装包。"; return false; }
            detail = $"程序文件校验通过（SHA-256 {actual}）。";
            return true;
        }
        catch (Exception ex)
        {
            detail = "程序文件校验无法完成：" + SafeLog.Redact(ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }
}
