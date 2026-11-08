using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// A virtualized paragraph needs real first-line layout. TextBlock.Margin cannot
/// express hanging indentation; a read-only FlowDocument paragraph can. Only
/// visible rows instantiate this presenter, and paired panes still share one row.
/// </summary>
public sealed class WordParagraphPresenter : RichTextBox
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments),
        typeof(IReadOnlyList<InlineDiffSegment>), typeof(WordParagraphPresenter), new PropertyMetadata(null, OnContentChanged));
    public static readonly DependencyProperty DiffKindProperty = DependencyProperty.Register(nameof(DiffKind),
        typeof(DiffKind), typeof(WordParagraphPresenter), new PropertyMetadata(DiffKind.Unchanged, OnAppearanceChanged));
    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(nameof(Side),
        typeof(WordDiffSide), typeof(WordParagraphPresenter), new PropertyMetadata(WordDiffSide.Base, OnAppearanceChanged));
    public static readonly DependencyProperty FirstLineIndentProperty = DependencyProperty.Register(nameof(FirstLineIndent),
        typeof(double), typeof(WordParagraphPresenter), new PropertyMetadata(0d, OnLayoutChanged));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight),
        typeof(double), typeof(WordParagraphPresenter), new PropertyMetadata(24d, OnLayoutChanged));
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(nameof(TextAlignment),
        typeof(TextAlignment), typeof(WordParagraphPresenter), new PropertyMetadata(TextAlignment.Left, OnLayoutChanged));
    private readonly Paragraph _paragraph = new() { Margin = new Thickness(0) };
    private DispatcherOperation? _pending;

    public WordParagraphPresenter()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new FlowDocument(_paragraph) { PagePadding = new Thickness(0) };
        Loaded += (_, _) => ScheduleRebuild();
        Unloaded += (_, _) => { _pending?.Abort(); _pending = null; };
        UpdateLayoutProperties();
    }

    public IReadOnlyList<InlineDiffSegment>? Segments { get => (IReadOnlyList<InlineDiffSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public DiffKind DiffKind { get => (DiffKind)GetValue(DiffKindProperty); set => SetValue(DiffKindProperty, value); }
    public WordDiffSide Side { get => (WordDiffSide)GetValue(SideProperty); set => SetValue(SideProperty, value); }
    public double FirstLineIndent { get => (double)GetValue(FirstLineIndentProperty); set => SetValue(FirstLineIndentProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    private static void OnContentChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((WordParagraphPresenter)value).ScheduleRebuild();
    private static void OnAppearanceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((WordParagraphPresenter)value).Restyle();
    private static void OnLayoutChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((WordParagraphPresenter)value).UpdateLayoutProperties();

    private void ScheduleRebuild()
    {
        if (_pending is not null) return;
        _pending = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _pending = null;
            _paragraph.Inlines.Clear();
            if (Segments is { } segments)
                foreach (var segment in segments)
                {
                    var run = new Run(segment.Text) { Tag = segment.IsChanged };
                    _paragraph.Inlines.Add(run);
                }
            Restyle();
        });
    }

    private void Restyle()
    {
        foreach (var run in _paragraph.Inlines.OfType<Run>())
        {
            if (run.Tag is not true) continue;
            run.FontWeight = FontWeights.SemiBold;
            run.SetResourceReference(TextElement.ForegroundProperty,
                DiffKind == DiffKind.Moved ? "Diff.Inline.Moved.Brush"
                : DiffKind == DiffKind.Conflict ? "Diff.Inline.Conflict.Brush"
                : Side == WordDiffSide.Base ? "Diff.Inline.Deleted.Brush" : "Diff.Inline.WordAdded.Brush");
        }
    }

    private void UpdateLayoutProperties()
    {
        _paragraph.TextIndent = FirstLineIndent;
        _paragraph.LineHeight = LineHeight;
        _paragraph.TextAlignment = TextAlignment;
        // Reserve the negative indent inside the text container rather than
        // drawing outside its clipping edge. The view offsets the container by
        // the same amount, so later lines remain at the original left indent.
        // RichTextBox transfers its Padding into FlowDocument.PagePadding while
        // attaching the editor; setting PagePadding alone is later overwritten.
        // Own the control padding so the first grapheme cannot lie at x < 0.
        Padding = new Thickness(Math.Max(0, -FirstLineIndent), 0, 0, 0);
    }
}
