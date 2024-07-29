namespace Rehike.CTLocaleScraper;

using Mono.Options;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

public static class Program
{
    /// <summary>
    /// The version of the compiler.
    /// </summary>
    public static readonly string VERSION =
        Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString()
        ?? "unknown";

    internal static string? g_languagePath = null;
    internal static string? g_cldrPath = null;
    internal static string? g_cldrManualName = null;

    internal static Mono.Options.OptionSet g_optionSet = new();

    internal static bool g_showHelp = false;

    public static void Main(string[] args)
    {
        ParseOptions(args);

        Console.Error.WriteLine("CoffeeTranslation Locale Scraper version " + VERSION);

        if (g_showHelp == true)
        {
            return;
        }

        if (g_languagePath == null)
        {
            Console.Error.WriteLine("Must specify language-path option. Exiting...");
            return;
        }

        if (g_cldrPath == null)
        {
            Console.Error.WriteLine("Must specify cldr-path option. Exiting...");
            return;
        }

        Console.Error.WriteLine($"Using language folder path: {g_languagePath}");
        Console.Error.WriteLine($"Using CLDR path: {g_cldrPath}");

        string languageName;
        if (g_cldrManualName is not null)
        {
            // If we manually specified an override (i.e. to en for en-US), then we
            // just want to go with that.
            languageName = g_cldrManualName;
            Console.Error.WriteLine($"Using manual CLDR language name: {g_cldrManualName}");
        }
        else
        {
            // Otherwise we'll infer the language name from the current folder.
            if (g_languagePath is not null)
            {
                string[] paths = g_languagePath.Replace('\\', '/').Split('/');
                
                // Rehike uses - for names: en-US
                // CLDR uses _ for names: en_US
                languageName = paths.Last().Replace("-", "_");
            }
            else
            {
                throw new Exception("Invalid g_languagePath value.");
            }
        }

        XDocument cldrXml = GetAppropriateCldrXml(languageName);
        WriteCoffeeTranslationFileFromCldrXml(cldrXml);
    }

    internal static void ParseOptions(string[] args)
    {
        g_optionSet
            .Add(
                "language-path=",
                "The folder to write output files to.",
                option =>
                {
                    g_languagePath = option;
                }
            )
            .Add(
                "cldr-path=",
                "The path storing CLDR definition files.",
                option =>
                {
                    g_cldrPath = option;
                }
            )
            .Add(
                "cldr-language-name=",
                "(Optional) Manually specify the name in the CLDR.",
                option =>
                {
                    g_cldrManualName = option;
                }
            )
            .Add(
                "help|h|?",
                "Shows this help menu.",
                option =>
                {
                    g_showHelp = true;
                    g_optionSet.WriteOptionDescriptions(Console.Error);
                }
            );

        g_optionSet.Parse(args);
    }

    internal static string GetCldrXmlPath(string languageName)
    {
        return $"{g_cldrPath}/common/main/{languageName}.xml";
    }

    internal static string GetCtPath(string endpoint)
    {
        return $"{g_languagePath}/{endpoint}.i18n";
    }

    internal static XDocument? GetAppropriateCldrXml(string languageName)
    {
        // We'll load the CLDR XML for the base language name as provided
        // to the file:
        string cldrXmlFilePath = GetCldrXmlPath(languageName);

        if (File.Exists(cldrXmlFilePath))
        {
            string fileContents = File.ReadAllText(cldrXmlFilePath);
            XDocument document = XDocument.Parse(fileContents);

            if (document.XPathSelectElement("/ldml/identity/language")?.Attribute("type")?.Value != languageName)
            {
                string value = document.XPathSelectElement("/ldml/identity/language").Attribute("type").Value;

                // If we have a different base language, then we'll load that in and merge the
                // existing document with it.
                string baseLanguageXmlPath = GetCldrXmlPath(value);
                string baseLanguageFileContents = File.ReadAllText(baseLanguageXmlPath);

                XDocument baseDocument = XDocument.Parse(baseLanguageFileContents);
                document?.Root?.Add(baseDocument?.Root?.Elements());
            }

            return document;
        }
        else
        {
            Console.Error.WriteLine($"File {cldrXmlFilePath} does not exist.");
            Environment.Exit(1);
        }

        return null;
    }

    internal static void WriteCoffeeTranslationFileFromCldrXml(XDocument cldr)
    {
        // Write language names:
        StringWriter langFileWriter = new();
        WriteFileHeaderComment(langFileWriter);

        foreach (XElement node in cldr.XPathSelectElements("/ldml/localeDisplayNames/languages/language"))
        {
            if (node.Attribute("alt") is not null)
            {
                // Skip alt strings.
                continue;
            }

            string languageName = node.Attribute("type").Value;
            string content = (node.FirstNode as XText).Value;
            string formattedContent = FormatStringLiteral(content);

            langFileWriter.WriteLine($"{languageName}: {formattedContent}");
        }

        string langFileContents = langFileWriter.ToString();
        File.WriteAllText(GetCtPath("language_names"), langFileContents);

        // Write country names:
        StringWriter countryFileWriter = new();
        WriteFileHeaderComment(countryFileWriter);

        foreach (XElement node in cldr.XPathSelectElements("/ldml/localeDisplayNames/territories/territory"))
        {
            if (node.Attribute("alt") is not null)
            {
                // Skip alt strings since we look them up anyway.
                continue;
            }

            string countryName = node.Attribute("type").Value;
            string content = (node.FirstNode as XText).Value;
            XElement? shortElement = cldr.XPathSelectElement(
                $"/ldml/localeDisplayNames/territories/territory[@type='{countryName}'][@alt='short']"
            );

            if (shortElement is not null)
            {
                string shortContent = (shortElement.FirstNode as XText).Value;
                if (shortContent.Length > 4)
                {
                    // We only use short strings if they're greater than 4 characters,
                    // so we get "United States" instead of "US", but "Palestine" instead
                    // of "Palestinian Territories".
                    content = shortContent;
                }
            }

            string formattedContent = FormatStringLiteral(content);

            countryFileWriter.WriteLine($"{countryName}: {formattedContent}");
        }

        string countryFileContents = countryFileWriter.ToString();
        File.WriteAllText(GetCtPath("country_names"), countryFileContents);
    }

    internal static void WriteFileHeaderComment(StringWriter writer)
    {
        writer.WriteLine("# Sourced from the Unicode Common Locale Data Repository (CLDR).");
        writer.WriteLine("# https://cldr.unicode.org/index");
        writer.WriteLine();
    }

    internal static string FormatStringLiteral(string srcValue)
    {
        srcValue = srcValue.Replace("\\", "\\\\");
        srcValue = srcValue.Replace("\n", "\\n");
        srcValue = srcValue.Replace("\'", "\\\'");
        srcValue = srcValue.Replace("\"", "\\\"");
        srcValue = "\"" + srcValue + "\"";

        return srcValue;
    }
}