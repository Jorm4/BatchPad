using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BatchPad.App.ViewModels;
using BatchPad.Core.Output;

namespace BatchPad.App.Views;

/// <summary>Fills a TextBlock from an output line: plain text, or one Run per ANSI span with source references as links.</summary>
public static class OutputText
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(OutputLineViewModel), typeof(OutputText), new PropertyMetadata(null, OnLineChanged));

    // Tuned for the dark output background rather than the classic console palette.
    private static readonly Brush?[] Palette =
    [
        null,
        Frozen("#6E6E6E"), Frozen("#F1707A"), Frozen("#6CCB5F"), Frozen("#E5C07B"),
        Frozen("#60A5FA"), Frozen("#C678DD"), Frozen("#56B6C2"), Frozen("#D4D4D4"),
        Frozen("#9A9A9A"), Frozen("#FF8A93"), Frozen("#8FE388"), Frozen("#FFD787"),
        Frozen("#8CC4FF"), Frozen("#E29BF0"), Frozen("#7FDCE6"), Frozen("#FFFFFF"),
    ];

    private static readonly Brush LinkBrush = Frozen("#60CDFF");

    public static OutputLineViewModel? GetLine(DependencyObject element) => (OutputLineViewModel?)element.GetValue(LineProperty);

    public static void SetLine(DependencyObject element, OutputLineViewModel? value) => element.SetValue(LineProperty, value);

    private static void OnLineChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block)
            return;
        var line = (OutputLineViewModel?)e.NewValue;
        List<SourceLinkViewModel> links = line?.Links is { } candidates ? [.. candidates.Where(l => l.Location is not null)] : [];
        if (line is null || (line.Spans is null && links.Count == 0))
        {
            block.Text = line?.Text ?? "";
            return;
        }
        block.Inlines.Clear();
        var spans = line.Spans ?? [new OutputSpan(line.Text, AnsiColor.Default, false)];
        var position = 0;
        var linkIndex = 0;
        Hyperlink? hyperlink = null;
        foreach (var span in spans)
        {
            var offset = 0;
            while (offset < span.Text.Length)
            {
                while (linkIndex < links.Count && End(links[linkIndex]) <= position)
                {
                    linkIndex++;
                    hyperlink = null;
                }
                var link = linkIndex < links.Count ? links[linkIndex] : null;
                var inLink = link is not null && link.Reference.Start <= position;
                var boundary = link is null ? int.MaxValue : inLink ? End(link) : link.Reference.Start;
                var length = Math.Min(span.Text.Length - offset, boundary - position);
                var run = Styled(new Run(span.Text.Substring(offset, length)), span);
                if (inLink)
                {
                    if (hyperlink is null)
                    {
                        hyperlink = new Hyperlink { Command = link!.OpenCommand, Foreground = LinkBrush, ToolTip = link.Location!.Path };
                        block.Inlines.Add(hyperlink);
                    }
                    hyperlink.Inlines.Add(run);
                }
                else
                {
                    block.Inlines.Add(run);
                }
                offset += length;
                position += length;
            }
        }
    }

    private static int End(SourceLinkViewModel link) => link.Reference.Start + link.Reference.Length;

    private static Run Styled(Run run, OutputSpan span)
    {
        if (Palette[(int)span.Color] is { } brush)
            run.Foreground = brush;
        if (span.Bold)
            run.FontWeight = FontWeights.Bold;
        return run;
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
