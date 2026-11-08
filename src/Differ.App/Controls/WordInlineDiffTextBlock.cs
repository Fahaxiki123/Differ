using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

public enum WordDiffSide
{
    Base,
    Mine
}

public sealed class WordInlineDiffTextBlock : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IEnumerable<InlineDiffSegment>),
            typeof(WordInlineDiffTextBlock),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnContentPropertyChanged));

    public static readonly DependencyProperty DiffKindProperty =
        DependencyProperty.Register(
            nameof(DiffKind),
            typeof(DiffKind),
            typeof(WordInlineDiffTextBlock),
            new FrameworkPropertyMetadata(
                DiffKind.Unchanged,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualPropertyChanged));

    public static readonly DependencyProperty SideProperty =
        DependencyProperty.Register(
            nameof(Side),
            typeof(WordDiffSide),
            typeof(WordInlineDiffTextBlock),
            new FrameworkPropertyMetadata(
                WordDiffSide.Base,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualPropertyChanged));

    public IEnumerable<InlineDiffSegment>? Segments
    {
        get => (IEnumerable<InlineDiffSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public DiffKind DiffKind
    {
        get => (DiffKind)GetValue(DiffKindProperty);
        set => SetValue(DiffKindProperty, value);
    }

    public WordDiffSide Side
    {
        get => (WordDiffSide)GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    private static void OnContentPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((WordInlineDiffTextBlock)dependencyObject).RebuildInlines();
    }

    private static void OnVisualPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (WordInlineDiffTextBlock)dependencyObject;
        foreach (var run in control.Inlines.OfType<Run>())
            if (run.Tag is true) control.ApplyChangedStyle(run);
    }

    private void RebuildInlines()
    {
        Inlines.Clear();
        Text = string.Empty;
        if (Segments is not IReadOnlyList<InlineDiffSegment> segments
            || segments.Count == 0)
        {
            return;
        }

        if (!segments.Any(segment => segment.IsChanged))
        {
            Text = string.Concat(segments.Select(segment => segment.Text));
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text) { Tag = segment.IsChanged };
            if (segment.IsChanged)
            {
                ApplyChangedStyle(run);
            }

            Inlines.Add(run);
        }
    }

    private void ApplyChangedStyle(Run run)
    {
        run.FontWeight = FontWeights.SemiBold;
        var resourceKey = DiffKind switch
        {
            DiffKind.Added => "Diff.Inline.WordAdded.Brush",
            DiffKind.Deleted => "Diff.Inline.Deleted.Brush",
            DiffKind.Moved => "Diff.Inline.Moved.Brush",
            DiffKind.Conflict => "Diff.Inline.Conflict.Brush",
            _ => Side == WordDiffSide.Base
                ? "Diff.Inline.Deleted.Brush"
                : "Diff.Inline.WordAdded.Brush"
        };
        run.SetResourceReference(TextElement.ForegroundProperty, resourceKey);
    }
}
