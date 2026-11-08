using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

public sealed class InlineDiffTextBlock : TextBlock
{
    private bool _hasChangedInlines;
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IEnumerable<InlineDiffSegment>),
            typeof(InlineDiffTextBlock),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualPropertyChanged));

    public static readonly DependencyProperty DiffKindProperty =
        DependencyProperty.Register(
            nameof(DiffKind),
            typeof(DiffKind),
            typeof(InlineDiffTextBlock),
            new FrameworkPropertyMetadata(
                DiffKind.Unchanged,
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

    private static void OnVisualPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((InlineDiffTextBlock)dependencyObject).RebuildInlines();
    }

    private void RebuildInlines()
    {
        if (Segments is not IReadOnlyList<InlineDiffSegment> segments
            || segments.Count == 0)
        {
            SetPlainText(string.Empty);
            return;
        }

        if (!segments.Any(segment => segment.IsChanged))
        {
            SetPlainText(segments.Count == 1
                ? segments[0].Text
                : string.Concat(segments.Select(segment => segment.Text)));
            return;
        }

        Inlines.Clear();
        Text = string.Empty;
        _hasChangedInlines = true;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsChanged)
            {
                InlineDiffPalette.ApplyChangedStyle(run, DiffKind);
            }

            Inlines.Add(run);
        }
    }

    private void SetPlainText(string value)
    {
        // Accessing Inlines creates WPF's rich TextContainer, even for an empty
        // collection. Most spreadsheet cells only need the simple Text path.
        if (_hasChangedInlines)
        {
            Inlines.Clear();
            _hasChangedInlines = false;
        }

        Text = value;
    }
}
