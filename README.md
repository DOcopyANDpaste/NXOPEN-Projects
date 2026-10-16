# BANxOpen.Ui.MaterialAssignment

The NX Block UI Styler dialog for assigning physical materials to bodies. All dialog logic is in
`MaterialAssignmentDialogPresenter`. `BlockUI.cs` only forwards callbacks to it. The material engine (planner,
rules, `ApplyPlan`) is in BANxOpen.Materials.

## Assignment logic

The basic rules:

- **Nothing touches the part until Apply / OK.** "Add to pending" and "Remove material" only list the change in the
  **Pending** tab. Until Apply, the **Current Assignment** tab keeps showing what each body really has, and flags the
  pending change in its *Pending* column.
- **One pending change per body.** If you act on the same body again, the latest action replaces the earlier one.
  This applies to assignments and removals alike.
- **Apply is final.** Cancel never reverts what was applied; it only discards what is still pending.

### A. "Add to pending" (material tree, right-click)

```
Add to pending (picked material [+ sheet metal row])
├─ No body selected? ─────────────────────────────► Error "Select at least one body". Pending unchanged.
├─ Selection not in the part any more? ──────────► Error "Refresh and try again". Pending unchanged.
├─ Body ALREADY HAS this material (same physical material; for a sheet metal body with a row picked,
│  the part's Sheet Metal Preferences are also that row)?
│   ├─ Not planned, not staged, no confirmation asked
│   ├─ Reported: "[Body] already has <material>"
│   └─ If the body had a pending change (another assignment, or a removal), it is dropped
│      (assigning what it already has means "leave it as it is")
├─ Every selected body already has it? ─────────► Info "Nothing to add to pending". Nothing staged.
├─ Plan the remaining bodies against the part's REAL current materials
├─ Plain material picked, but a non-blocked sheet metal body is selected?
│                                                 ► Error "pick a sheet metal row". Pending unchanged.
├─ Sheet metal conflict (another sheet metal body, real or pending, has a different material/row;
│  a body pending removal counts as having no material)?
│                                                 ► Error (conflict details). Pending unchanged.
├─ For each body in the plan:
│   ├─ Blocked by a rule ────────────────────────► reported "blocked: <reason>"; NOT staged;
│   │                                               its earlier pending change, if any, is KEPT
│   ├─ Needs confirmation (reassignment, coating, thickness) → one prompt for the entry
│   │   ├─ Yes ──────────────────────────────────► staged; the confirmation is remembered
│   │   └─ No ───────────────────────────────────► reported "skipped: not confirmed"; NOT staged;
│   │                                               its earlier pending change, if any, is KEPT
│   └─ Ok (with or without a warning) ───────────► staged
├─ Nothing staged? ──────────────────────────────► Error "<material> was not added to pending" + reasons.
└─ Something staged:
    ├─ Only the staged bodies are taken out of earlier pending entries (entries left empty are dropped)
    │   A pending removal replaced this way is reported: "[Body] its pending removal was replaced"
    ├─ The new entry (staged bodies only) is added to Pending
    ├─ One info box for anything left out (already has it / blocked / declined), replaced, or warned about
    ├─ Pending tab, Status column:  "Pending"  |  "Pending: <warning>"  |  "Pending — confirmed: <reason>"
    └─ Current Assignment tab: materials unchanged; Pending column flags the change
         body row  → "→ <new material [row]>"   (+ " (confirmed)" when a confirmation was given)
         group row → "<n> pending"
```

### B. "Remove material" (Current Assignment tab, right-click)

```
Remove material (the clicked rows' bodies)
├─ Body has no material ─────────────────────────► skipped: "[Body] has no material"
├─ Body pending an assignment to Y ─────────────► the assignment is REPLACED by the removal
│                                                   (reported: "[Body] its pending assignment to Y was replaced")
└─ Added to the single "Remove material" entry in Pending (no confirmation; Apply is the confirmation)
     Pending tab:  "Remove material" ▸ Body   Status "Pending — currently <material>"
     Current tab:  Pending column "→ (remove material)"; group row "<n> pending"
```

### C. Apply / OK

```
Apply / OK
├─ Pending not empty → apply the pending list:
│   ├─ Re-read the part
│   ├─ The removal first (each body is in only one entry, so the order is only for predictability):
│   │   ├─ Bodies no longer in the part ──────────► reported and dropped
│   │   ├─ Cleared ────────────────────────────────► leave Pending
│   │   ├─ Partial failure ───────────────────────► bodies that still have a material stay pending
│   │   └─ Failed ─────────────────────────────────► all stay pending ("Not applied — <NX message>")
│   ├─ Then each assignment, in order:
│   │   ├─ Bodies no longer in the part ──────────► reported and dropped
│   │   ├─ Re-plan against the part as it is now
│   │   ├─ Sheet metal conflict now? ─────────────► whole entry stays pending ("Not applied — conflicts …")
│   │   ├─ Bodies that newly need confirmation → prompt (earlier answers are reused)
│   │   ├─ Bodies blocked or declined now ────────► stay pending ("Not applied — blocked: … / not confirmed")
│   │   ├─ Apply the rest to the part
│   │   │   ├─ Ok ─────────────────────────────────► they leave Pending
│   │   │   ├─ Partial failure ───────────────────► bodies that now carry the material leave Pending;
│   │   │   │                                       the rest stay ("Not applied — <NX message>")
│   │   │   └─ Failed ─────────────────────────────► all stay pending ("Not applied — <NX message>")
│   │   └─ Re-read the part (the next entry is planned against what this one did)
│   ├─ One message: error if anything is still pending, info if everything applied but there were notes
│   └─ Both trees refresh: Current shows the new materials, Pending shows only what is left
│       ├─ Apply: the dialog stays open
│       └─ OK: anything still pending? → the dialog stays open, so you can see what did not apply
│                                     else → closes
├─ Pending empty, sheet metal row picked, bodies selected → Add to pending + apply, as above
├─ Pending empty, sheet metal row picked, no solid body in the part → sets only the Sheet Metal Preferences
└─ Pending empty, nothing picked → Apply: error "Nothing is pending"; OK: closes
```

### D. Other actions

```
Remove from pending / Clear all pending ► removed from the list only; the part is not touched
Cancel / close ► anything still pending is discarded, after "N pending change(s) … will be discarded. Close
                 anyway?" when there is any. Applied changes are final and are never reverted.
Refresh        ► re-reads the part and drops pending bodies that no longer exist (Apply re-plans the rest)
```

## Trees

Every tree (Material, Current Assignment, Pending) opens with its top-level rows expanded (level 1), and it is
expanded again after each rebuild.

## Changing messages

Text lives with whatever produces it, in one of three layers. The Bead dialog follows the same rule, so a message
shared by both dialogs is written once. The pattern is in the Foundation skill
(`BANxOpen.Foundation/Skills/nxopen-csharp-patterns/references/common.md` §8).

| To change… | Edit |
|---|---|
| A rule outcome, the Sheet Metal Preferences check, a configuration or SPEC-workbook error, a part-service failure | The library's describer: `MaterialsText.cs` (BANxOpen.Materials.Core), `SheetMetalText.cs` (BANxOpen.SheetMetal.Core), `FoundationText.cs` (BANxOpen.Foundation.Core) |
| Message box titles, "could not start", the unexpected-error message, how an NX error is shown | `NxDialogText.cs` (BANxOpen.Foundation.NxAdapters) |
| Anything specific to this dialog: menus, columns, Pending statuses, staging and apply messages, prompts | [`UiText.cs`](BANxOpen.Ui.MaterialAssignment/UiText.cs) |

Edit the text and rebuild. Keep the `{…}` placeholders: they are the values the text is filled with.

- The dialog shows a library's message as-is (`RuleOutcome.Message`, `OperationResult.Message`) and never rewords it.
  Rules and services also report a stable code (`ReasonCode` / `ErrorCode`, listed in `MaterialsCodes`,
  `SheetMetalCodes`, `FoundationCodes`) and the values behind the message (`RuleOutcome.Args`).
- A describer's members are virtual. A tool could pass its own subclass to `MaterialEngine.Create` /
  `SheetMetalServices.Create` to change a wording without touching the library. None does today.
- Log lines (the Listing Window) are not user text and stay where they are written.
