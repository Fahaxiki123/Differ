using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// Read-only inline diff renderer that supports real mouse text selection and
/// the native Ctrl+C clipboard path. Used in the bottom detail panes only: the
/// virtualized row cells keep the much cheaper
/// <see cref="InlineDiffTextBlock" />.
/// </summary>
public sealed class SelectableDiffTextBox : RichTextBox
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IEnumerable<InlineDiffSegment>),
            typeof(SelectableDiffTextBox),
            new FrameworkPropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty DiffKindProperty =
        DependencyProperty.Register(
            nameof(DiffKind),
            typeof(DiffKind),
            typeof(SelectableDiffTextBox),
            new FrameworkPropertyMetadata(
                DiffKind.Unchanged,
                OnVisualPropertyChanged));

    public SelectableDiffTextBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        AcceptsReturn = false;
        AcceptsTab = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        IsInactiveSelectionHighlightEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        RebuildDocument();
    }

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
        ((SelectableDiffTextBox)dependencyObject).RebuildDocument();
    }

    private void RebuildDocument()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0)
        };

        if (Segments is IReadOnlyList<InlineDiffSegment> segments
            && segments.Count > 0)
        {
            foreach (var segment in segments)
            {
                var run = new Run(segment.Text);
                if (segment.IsChanged)
                {
                    InlineDiffPalette.ApplyChangedStyle(run, DiffKind);
                }

                paragraph.Inlines.Add(run);
            }
        }

        Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0)
        };
    }
}
