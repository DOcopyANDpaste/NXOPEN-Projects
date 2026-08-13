using NxOpen.Foundation.Contracts.Materials;

namespace NxAdapters.Ui;

/// <summary>What <see cref="BlockAccessor"/> calls back into when the user touches a tree. Implemented by
/// <see cref="MaterialAssignmentDialogPresenter"/>.
///
/// It exists so the accessor can register the NXOpen tree handlers — translating <c>Node</c> to the domain
/// object it was rendered from on the way through — without the presenter ever seeing a <c>Node</c>,
/// <c>Tree</c> or <c>TreeListMenu</c>. Menus are exchanged as <see cref="TreeMenuItem"/> records for the
/// same reason: the presenter decides what the menu contains and what is enabled, the accessor builds it.
///
/// Tree interaction cannot go through <c>update_cb</c> — Block UI Styler does not route tree events there —
/// so this is a genuinely separate callback surface from <see cref="MaterialAssignmentDialogPresenter.OnUpdate"/>,
/// not a duplicate of it.</summary>
public interface ITreeInteractionSink
{
    void OnMaterialSelected(Material? material);

    /// <summary>Fires on hover (Tree.SetOnPreSelectHandler), which is what drives the thumbnail preview.</summary>
    void OnMaterialHovered(Material? material);

    string OnMaterialTooltip(Material material);

    /// <summary>Double-click on a material row.</summary>
    void OnMaterialDefaultAction(Material material);

    IReadOnlyList<TreeMenuItem> BuildMaterialMenu(Material? clicked);
    void OnMaterialMenuCommand(int menuItemId, IReadOnlyList<Material> targets);

    void OnAssignmentSelected(AssignmentRowRef? row);
    IReadOnlyList<TreeMenuItem> BuildAssignmentMenu(AssignmentRowRef? clicked);
    void OnAssignmentMenuCommand(int menuItemId, IReadOnlyList<AssignmentRowRef> targets);

    void OnPendingSelected(PendingRowRef? row);
    IReadOnlyList<TreeMenuItem> BuildPendingMenu(PendingRowRef? clicked);
    void OnPendingMenuCommand(int menuItemId, IReadOnlyList<PendingRowRef> targets);
}
