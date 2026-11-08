using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Differ.App.Resources;
using Differ.Core.Algorithms;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// Most Word paragraphs need only TextBlock. Instantiate a FlowDocument only
/// for a real first-line offset or explicit character-selection interaction.
/// There is no collapsed RichTextBox in the ordinary virtualized row template.
/// </summary>
public sealed class WordParagraphHost : ContentControl
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments),
        typeof(IReadOnlyList<InlineDiffSegment>), typeof(WordParagraphHost), new PropertyMetadata(null, OnSegmentsChanged));
    public static readonly DependencyProperty DiffKindProperty = DependencyProperty.Register(nameof(DiffKind),
        typeof(DiffKind), typeof(WordParagraphHost), new PropertyMetadata(DiffKind.Unchanged, OnAppearanceChanged));
    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(nameof(Side),
        typeof(WordDiffSide), typeof(WordParagraphHost), new PropertyMetadata(WordDiffSide.Base, OnAppearanceChanged));
    public static readonly DependencyProperty FirstLineIndentProperty = DependencyProperty.Register(nameof(FirstLineIndent),
        typeof(double), typeof(WordParagraphHost), new PropertyMetadata(0d, OnLayoutChanged));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight),
        typeof(double), typeof(WordParagraphHost), new PropertyMetadata(24d, OnLayoutChanged));
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(nameof(TextAlignment),
        typeof(TextAlignment), typeof(WordParagraphHost), new PropertyMetadata(TextAlignment.Left, OnLayoutChanged));
    private WordInlineDiffTextBlock? _plain;
    private WordParagraphPresenter? _rich;
    private bool _selectingCharacters;

    public WordParagraphHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        Focusable = false;
        ToolTip = WordReviewStrings.SelectCharactersHint;
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewKeyDown += OnKeyDown;
        Unloaded += (_, _) =>
        {
            if (!_selectingCharacters) return;
            _selectingCharacters = false;
            ApplyContent();
        };
        ApplyContent();
    }

    public IReadOnlyList<InlineDiffSegment>? Segments { get => (IReadOnlyList<InlineDiffSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public DiffKind DiffKind { get => (DiffKind)GetValue(DiffKindProperty); set => SetValue(DiffKindProperty, value); }
    public WordDiffSide Side { get => (WordDiffSide)GetValue(SideProperty); set => SetValue(SideProperty, value); }
    public double FirstLineIndent { get => (double)GetValue(FirstLineIndentProperty); set => SetValue(FirstLineIndentProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    public void EnableCharacterSelection()
    {
        _selectingCharacters = true;
        ApplyContent();
        var selected = _rich!;
        // ContentPresenter attaches the replacement on the next layout pass.
        // Focus after that pass so F2/context-menu activation works on the
        // first invocation rather than requiring a second pointer click.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsLoaded && ReferenceEquals(Content, selected)) selected.Focus();
        });
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount != 2 || _rich is not null) return;
        EnableCharacterSelection();
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.F2) return;
        EnableCharacterSelection();
        args.Handled = true;
    }

    private static void OnSegmentsChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        var host = (WordParagraphHost)value;
        host._selectingCharacters = false;
        host.ApplyContent();
    }

    private static void OnLayoutChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((WordParagraphHost)value).ApplyContent();
    private static void OnAppearanceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((WordParagraphHost)value).ApplyAppearance();

    private void ApplyContent()
    {
        if (Math.Abs(FirstLineIndent) > 0.01 || _selectingCharacters)
        {
            _rich ??= new WordParagraphPresenter();
            _rich.LineHeight = LineHeight;
            _rich.TextAlignment = TextAlignment;
            _rich.FirstLineIndent = FirstLineIndent;
            _rich.Segments = Segments;
            Content = _rich;
        }
        else
        {
            _plain ??= new WordInlineDiffTextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            _plain.LineHeight = LineHeight;
            _plain.TextAlignment = TextAlignment;
            _plain.Segments = Segments;
            Content = _plain;
            // Release the document, text editor services and undo/selection
            // objects when the virtualized host switches to another paragraph.
            _rich = null;
        }
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (_rich is not null) { _rich.DiffKind = DiffKind; _rich.Side = Side; }
        else if (_plain is not null) { _plain.DiffKind = DiffKind; _plain.Side = Side; }
    }
}
