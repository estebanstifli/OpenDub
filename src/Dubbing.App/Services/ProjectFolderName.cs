using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dubbing.App.Services;

public static class ProjectFolderName
{
    public static string Normalize(string name)
    {
        var ascii = new StringBuilder();
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            ascii.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }
        string result = Regex.Replace(ascii.ToString(), "-+", "-").Trim('-');
        if (result.Length > 80) result = result[..80].TrimEnd('-');
        if (result.Length == 0) result = "Project";
        if (Regex.IsMatch(result, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) result = "Project-" + result;
        return result;
    }
}
