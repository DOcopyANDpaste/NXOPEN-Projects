using NXOpen.BlockStyler;

namespace NxAdapters.Ui;

/// <summary>
/// GENERATED (CONCEPTUAL PLACEHOLDER) — stands in for what Block UI Styler will actually emit once the
/// real <c>.dlx</c> exists. It cannot be hand-authored for real: the Styler tool requires an NX
/// installation, which this session does not have (see the plan's §2/§4 dynamic-tab/tile spike). Do not
/// treat this as the real generated class — actual Styler output includes dialog lifecycle/plumbing this
/// file does not attempt to reproduce faithfully (block declarations, Dispose, real Show() semantics).
///
/// What IS real and must survive regeneration: the five callback delegations below, banner-marked per
/// Skills/with-block-ui.md §1. Nothing else belongs in the generated file — all logic lives in
/// <see cref="MaterialAssignmentDialogPresenter"/>.</summary>
public sealed class MaterialAssignmentDialog
{
    // VERIFY: placeholder plumbing — the real Styler-generated class provides its own BlockDialog-backed
    // construction/Show()/Dispose(); shown here only so MaterialAssignmentCommand's composition-root code
    // reads coherently until the real generated file exists.
    internal BlockDialog TheDialog { get; } = null!;

    // Set by MaterialAssignmentCommand (the composition root) right after construction, before Show().
    internal MaterialAssignmentDialogPresenter Presenter { get; set; } = null!;

    public void Show()
    {
        // VERIFY: real show/lifecycle call the Styler tool emits.
    }

    // >>> HAND-EDITED DELEGATIONS — re-add these after any Styler regeneration <<<
    public void dialogShown_cb() => Presenter.OnDialogShown();

    // VERIFY: UIBlock.Name is assumed to be the block's string ID — matches BlockAccessor's ID constants.
    public void update_cb(UIBlock block) => Presenter.OnUpdate(block.Name);

    public int apply_cb() => Presenter.OnApply();

    public int ok_cb() => Presenter.OnOk();

    public void cancel_cb() => Presenter.OnCancel();
    // >>> END HAND-EDITED <<<
}
