using System.Net;
using System.Text;

namespace iSay.Desktop.Services;

/// <summary>最终整理使用标准段落片段，保留空行，不把换行交给持续组合的位置换算。</summary>
internal static class RefinementClipboardFormat
{
    internal static byte[] Html(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var fragment = "<div data-pm-slice=\"0 0 []\">" + string.Concat(normalized.Split('\n').Select(line =>
            "<p>" + (line.Length == 0 ? "<br>" : WebUtility.HtmlEncode(line)) + "</p>")) + "</div>";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        static string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
            $"Version:1.0\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";
        var startHtml = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return Encoding.UTF8.GetBytes(Header(startHtml, endHtml, startFragment, endFragment) + before + fragment + after + "\0");
    }
}
