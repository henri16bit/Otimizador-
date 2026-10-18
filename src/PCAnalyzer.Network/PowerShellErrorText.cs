using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PCAnalyzer.Network;

internal static class PowerShellErrorText
{
    internal static string Read(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "A consulta não retornou detalhes. Consulte o log técnico de rede.";
        var text = error.Trim();
        if (text.Contains("<Objs", StringComparison.Ordinal) || text.StartsWith("#< CLIXML", StringComparison.Ordinal))
        {
            try
            {
                var start = text.IndexOf('<', text.IndexOf("CLIXML", StringComparison.Ordinal) is var marker && marker >= 0 ? marker + 6 : 0);
                using var reader = XmlReader.Create(new StringReader(text[start..]), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
                var document = XDocument.Load(reader);
                text = string.Join(" ", document.Descendants().Where(e => e.Name.LocalName == "S" &&
                    (string?)e.Attribute("S") == "Error").Select(e => e.Value));
                if (string.IsNullOrWhiteSpace(text)) return "A consulta não retornou detalhes de erro. Consulte o log técnico de rede.";
            }
            catch (Exception ex) when (ex is XmlException or ArgumentException)
            {
                return "A resposta do PowerShell não pôde ser interpretada. Consulte o log técnico de rede.";
            }
        }
        text = Regex.Replace(text, "_x([0-9a-fA-F]{4})_", match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 600 ? text : text[..600] + "… Consulte o log técnico de rede.";
    }

    internal static void Log(string raw)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCAnalyzer", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "network.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\n{raw}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
