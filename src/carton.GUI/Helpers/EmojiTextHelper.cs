using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System.Text.RegularExpressions;

namespace carton.GUI.Helpers;

public class EmojiTextHelper
{
    // Match Regional Indicator Symbols combinations (Emoji Flags)
    private static readonly Regex FlagRegex = new Regex(@"(\uD83C[\uDDE6-\uDDFF]){2}", RegexOptions.Compiled);

    public static readonly AttachedProperty<string> TextProperty =
        AvaloniaProperty.RegisterAttached<EmojiTextHelper, TextBlock, string>("Text");

    public static readonly AttachedProperty<string> PrefixProperty =
        AvaloniaProperty.RegisterAttached<EmojiTextHelper, TextBlock, string>("Prefix");

    static EmojiTextHelper()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
        PrefixProperty.Changed.AddClassHandler<TextBlock>(OnTextChanged);
    }

    public static string GetText(AvaloniaObject element) => element.GetValue(TextProperty);
    public static void SetText(AvaloniaObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetPrefix(AvaloniaObject element) => element.GetValue(PrefixProperty);
    public static void SetPrefix(AvaloniaObject element, string value) => element.SetValue(PrefixProperty, value);

    private static void OnTextChanged(TextBlock textBlock, AvaloniaPropertyChangedEventArgs e)
    {
        var text = GetText(textBlock);
        var prefix = GetPrefix(textBlock);
        
        string fullText = (prefix ?? "") + (text ?? "");
        
        if (textBlock.Inlines != null)
        {
            textBlock.Inlines.Clear();
        }

        if (string.IsNullOrEmpty(fullText))
        {
            textBlock.Text = string.Empty;
            return;
        }

        var matches = FlagRegex.Matches(fullText);
        if (matches.Count == 0)
        {
            textBlock.Text = fullText;
            return;
        }

        if (textBlock.Inlines == null)
            textBlock.Inlines = new InlineCollection();

        var emojiFont = new FontFamily("avares://carton/Assets/Fonts#Twemoji COLRv0");

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run { Text = fullText.Substring(lastIndex, match.Index - lastIndex) });
            }

            textBlock.Inlines.Add(new Run
            {
                Text = match.Value,
                FontFamily = emojiFont,
                FontWeight = FontWeight.Normal,
                FontStyle = FontStyle.Normal
            });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < fullText.Length)
        {
            textBlock.Inlines.Add(new Run { Text = fullText.Substring(lastIndex) });
        }
    }
}
