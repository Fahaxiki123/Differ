using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Differ.App.ViewModels;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

public sealed class InlineDiffTextBlock : TextBlock
{
    private bool _hasChangedInlines;
    public static readonly DependencyProperty DiffContentProperty = DependencyProperty.Register(
        nameof(DiffContent), typeof(InlineDiffContent), typeof(InlineDiffTextBlock),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

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

    public InlineDiffContent? DiffContent
    {
        get => (InlineDiffContent?)GetValue(DiffContentProperty);
        set => SetValue(DiffContentProperty, value);
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
        var content = DiffContent;
        var segments = content?.Segments ?? Segments as IReadOnlyList<InlineDiffSegment>;
        if (segments is null || segments.Count == 0)
        {
            SetPlainText(string.Empty);
            return;
        }

        if (segments.Count == 1 && !segments[0].IsChanged)
        {
            SetPlainText(segments[0].Text);
            return;
        }

        if (!segments.Any(segment => segment.IsChanged))
        {
            SetPlainText(string.Concat(segments.Select(segment => segment.Text)));
            return;
        }

        Inlines.Clear();
        Text = string.Empty;
        _hasChangedInlines = true;
        var kind = content?.Kind ?? DiffKind;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsChanged)
            {
                InlineDiffPalette.ApplyChangedStyle(run, kind);
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
