using ColorCode;
using ColorCode.Styling;
using Microsoft.UI.Xaml;

namespace PiStation.App.Views;

internal static class SyntaxHighlighting
{
    public static RichTextBlockFormatter CreateFormatter(ElementTheme theme)
    {
        if (theme != ElementTheme.Dark)
        {
            return new RichTextBlockFormatter(ElementTheme.Light);
        }

        // ColorCode's dark preset retains several light-theme token colors.
        // Use a consistent readable palette on both code and diff surfaces.
        var styles = StyleDictionary.DefaultDark;
        foreach (var style in styles)
        {
            style.Foreground = style.ReferenceName switch
            {
                "comment" or "htmlComment" or "xmlComment" or "xmlDocComment" or "xmlDocTag" => "#FF98B887",
                "string" or "stringCSharpVerbatim" or "jsonString" or "markdownCode" or "xmlCDataSection" => "#FFDCAF96",
                "keyword" or "controlKeyword" or "preprocessorKeyword" or "pseudoKeyword" or "predefined" or
                "htmlTagDelimiter" or "htmlOperator" or "htmlAttributeValue" or "xmlAttributeQuotes" or
                "xmlAttributeValue" or "cssPropertyValue" or "markdownHeader" => "#FF80BFFF",
                "className" or "type" or "typeVariable" or "namespace" or "constructor" or "powershellType" => "#FF79D6C0",
                "number" or "jsonNumber" or "builtinValue" => "#FFBAD9A8",
                "htmlElementName" or "htmlEntity" or "cssSelector" or "powershellVariable" => "#FFFFA799",
                "htmlAttributeName" or "xmlAttribute" or "jsonKey" or "cssPropertyName" or "attribute" or
                "powershellAttribute" => "#FFB9DFFF",
                "jsonConst" or "sqlSystemFunction" => "#FFD6AFF2",
                "builtinFunction" or "powershellCommand" => "#FFE6D793",
                _ => "#FFE0E0E0",
            };
        }

        return new RichTextBlockFormatter(styles);
    }
}
