using System.Text;
using Microsoft.Web.WebView2.Core;

namespace MediaDownloadManager.Core;

/// <summary>Stores cookies captured by the in-app login window as a Netscape cookies.txt for yt-dlp.</summary>
public static class CookieStore
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaDownloadManager");

    public static string ProfileDir => Path.Combine(Dir, "webview-profile");
    public static string FilePath => Path.Combine(Dir, "cookies.txt");

    public static bool Exists => File.Exists(FilePath) && new FileInfo(FilePath).Length > 50;

    public static bool HasLoginCookie(IEnumerable<CoreWebView2Cookie> cookies)
        => cookies.Any(c => c.Name is "SID" or "__Secure-3PSID" or "LOGIN_INFO");

    public static int Save(IEnumerable<CoreWebView2Cookie> cookies)
    {
        Directory.CreateDirectory(Dir);
        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        var n = 0;
        foreach (var c in cookies)
        {
            var domain = c.Domain;
            var includeSub = domain.StartsWith('.') ? "TRUE" : "FALSE";
            var expires = c.IsSession || c.Expires == default
                ? 0L
                : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();
            var prefix = c.IsHttpOnly ? "#HttpOnly_" : "";
            sb.Append(prefix).Append(domain).Append('\t')
              .Append(includeSub).Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
              .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t')
              .Append(expires).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).AppendLine();
            n++;
        }
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        return n;
    }
}
