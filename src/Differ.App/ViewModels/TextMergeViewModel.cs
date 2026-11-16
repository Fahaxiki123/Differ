using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Differ.App.Resources;
using Differ.Formats.Text;

namespace Differ.App.ViewModels;

/// <summary>Projects the authoritative merge session without a second UI-side merge algorithm.</summary>
public sealed class TextMergeViewModel : ObservableObject
{
    private static readonly CompositeFormat CountFormat = CompositeFormat.Parse(MergeStrings.CountFormat);
    private bool _autoAdvance;
    public TextMergeViewModel(TextMergeSession session, string? mineName = null, string? baseName = null,
        string? theirsName = null, string? mergedName = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        MineName = mineName ?? Path.GetFileName(session.MinePath);
        BaseName = baseName ?? Path.GetFileName(session.BasePath);
        TheirsName = theirsName ?? Path.GetFileName(session.TheirsPath);
        MergedName = mergedName ?? Path.GetFileName(session.OutputPath);
        SelectedBlock = session.Blocks.FirstOrDefault(block => block.IsConflict)?.Index ?? -1;
    }

    public TextMergeSession Session { get; }
    public string MineName { get; }
    public string BaseName { get; }
    public string TheirsName { get; }
    public string MergedName { get; }
    public string Title => MergedName + " — " + MergeStrings.Title;
    public int SelectedBlock { get; private set; }
    public bool AutoAdvance { get => _autoAdvance; set => SetProperty(ref _autoAdvance, value); }
    public string Status => string.Format(CultureInfo.CurrentCulture, CountFormat,
        Session.UnresolvedCount, Session.Blocks.Count(block => block.IsConflict));

    public void Navigate(int direction)
    {
        var conflicts = Session.Blocks.Where(block => block.IsConflict).Select(block => block.Index).ToArray();
        if (conflicts.Length == 0) return;
        var current = Array.IndexOf(conflicts, SelectedBlock);
        SelectedBlock = current < 0 ? conflicts[direction < 0 ? ^1 : 0]
            : conflicts[(current + (direction < 0 ? -1 : 1) + conflicts.Length) % conflicts.Length];
        Refresh();
    }

    public bool Select(int index)
    {
        if (!Session.Blocks.Any(block => block.Index == index && block.IsConflict)) return false;
        SelectedBlock = index;
        Refresh();
        return true;
    }

    public bool SelectResultOffset(int utf16Offset)
    {
        if (utf16Offset < 0 || utf16Offset > Session.ResultText.Length) return false;
        var candidates = Session.Blocks.Where(block => block.IsConflict
            && (block.ResultLength == 0 ? utf16Offset == block.ResultOffset
                : utf16Offset >= block.ResultOffset && utf16Offset < block.ResultOffset + block.ResultLength)).ToArray();
        var selected = candidates.FirstOrDefault(block => block.Index == SelectedBlock)
            ?? candidates.MinBy(block => block.Index);
        return selected is not null && Select(selected.Index);
    }

    public bool Apply(string command, long version)
    {
        var before = Session.Blocks.ToArray();
        var beforeText = Session.ResultText;
        var originallyUnresolved = before.Any(block => block.Index == SelectedBlock && block.IsConflict && !block.IsResolved);
        var accepted = command switch
        {
            "mine" => Session.Choose(SelectedBlock, MergeChoice.Mine, version),
            "base" => Session.Choose(SelectedBlock, MergeChoice.Base, version),
            "theirs" => Session.Choose(SelectedBlock, MergeChoice.Theirs, version),
            "confirm" => Session.Confirm(SelectedBlock, version),
            "undo" => Session.Undo(version),
            "redo" => Session.Redo(version),
            _ => false
        };
        if (accepted)
        {
            if (command is "undo" or "redo") SelectHistoryRegion(before, beforeText);
            else if (AutoAdvance && originallyUnresolved && command is "mine" or "base" or "theirs" or "confirm")
                SelectNextUnresolved();
        }
        Refresh();
        return accepted;
    }

    private void SelectNextUnresolved()
    {
        var unresolved = Session.Blocks.Where(block => block.IsConflict && !block.IsResolved).ToArray();
        var next = unresolved.FirstOrDefault(block => block.Index > SelectedBlock) ?? unresolved.FirstOrDefault();
        if (next is not null) SelectedBlock = next.Index;
    }

    private void SelectHistoryRegion(TextMergeBlock[] before, string beforeText)
    {
        var after = Session.Blocks;
        var changed = after.Where(block => block.IsConflict && (block.IsResolved != before[block.Index].IsResolved
            || block.Choice != before[block.Index].Choice || block.CanChoose != before[block.Index].CanChoose)).ToArray();
        if (changed.Length == 0)
        {
            // Offsets of later conflicts can shift without their own content changing.
            // Compare each block's text, so undoing one edit never jumps to its neighbour.
            changed = after.Where(block => block.IsConflict && !Session.ResultText.AsSpan(block.ResultOffset, block.ResultLength)
                .SequenceEqual(beforeText.AsSpan(before[block.Index].ResultOffset, before[block.Index].ResultLength))).ToArray();
        }
        var target = changed.FirstOrDefault(block => block.Index == SelectedBlock) ?? changed.FirstOrDefault();
        if (target is not null) SelectedBlock = target.Index;
    }

    public bool UpdateResult(string text, long version)
    {
        var accepted = Session.UpdateResult(text, version);
        Refresh();
        return accepted;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(SelectedBlock));
        OnPropertyChanged(nameof(Session));
    }
}
