using Core.Bodies;

namespace NxAdapters.Ui;

/// <summary>What one row of <c>CurrentAssignmentTree</c> stands for. A root row names a material in use and
/// carries <see cref="Body"/> null; a child row names one body under it. Uniform so a context-menu command
/// can act on either without the caller caring which was clicked — "select these bodies" means all of the
/// row's bodies on a root and just the one on a child.</summary>
public sealed record AssignmentRowRef(MaterialUsageRow Row, IReadOnlyList<BodyInfo> Bodies, BodyInfo? Body)
{
    public static AssignmentRowRef ForMaterial(MaterialUsageRow row, IReadOnlyList<BodyInfo> bodies) =>
        new(row, bodies, null);

    public static AssignmentRowRef ForBody(MaterialUsageRow row, BodyInfo body) =>
        new(row, new[] { body }, body);
}

/// <summary>The same idea for <c>PendingAssignmentTree</c>: a root row is a whole staged entry, a child row
/// is one body within it.</summary>
public sealed record PendingRowRef(PendingAssignmentEntry Entry, PendingBodyRow? Row);

/// <summary>One context-menu entry, decided by the presenter and built into a <c>TreeListMenu</c> by
/// <see cref="BlockAccessor"/>. Keeping it a plain record is what lets the presenter own the menu's content
/// and enablement without touching an NXOpen type.</summary>
public sealed record TreeMenuItem(int Id, string Text, bool Enabled = true)
{
    public const int SeparatorId = -1;

    public static readonly TreeMenuItem Separator = new(SeparatorId, string.Empty);

    public bool IsSeparator => Id == SeparatorId;
}
