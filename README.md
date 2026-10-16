# BANxOpen.Ui.MaterialAssignment

The NX Block UI Styler dialog for assigning physical materials to bodies. All dialog logic is in
`MaterialAssignmentDialogPresenter`. `BlockUI.cs` only forwards callbacks to it. The material engine (planner,
rules, `ApplyPlan`) is in BANxOpen.Materials.

## Assignment logic

The basic rule is that **"Add to pending" never touches the part**. The part changes only on **Apply / OK**. Until
then, the **Current Assignment** tab keeps showing what each body really has, and flags the pending change in its
*Pending* column.

### A. "Add to pending" (material tree, right-click)

```
Add to pending (picked material [+ sheet metal row])
├─ No body selected? ─────────────────────────────► Error "Select at least one body". Pending unchanged.
├─ Selection not in the part any more? ──────────► Error "Refresh and try again". Pending unchanged.
├─ Body ALREADY HAS this material (same physical material; for a sheet metal body with a row picked,
│  the part's Sheet Metal Preferences are also that row)?
│   ├─ Not planned, not staged, no confirmation asked
│   ├─ Reported: "[Body] already has <material>"
│   └─ If the body was pending under a different material, that pending change is removed
│      (assigning what it already has means "leave it as it is")
├─ Every selected body already has it? ─────────► Info "Nothing to add to pending". Nothing staged.
├─ Plan the remaining bodies against the part's REAL current materials
├─ Plain material picked, but a non-blocked sheet metal body is selected?
│                                                 ► Error "pick a sheet metal row". Pending unchanged.
├─ Sheet metal conflict (another sheet metal body, real or pending, has a different material/row)?
│                                                 ► Error (conflict details). Pending unchanged.
├─ For each body in the plan:
│   ├─ Blocked by a rule ────────────────────────► reported "blocked: <reason>"; NOT staged;
│   │                                               its earlier pending entry, if any, is KEPT
│   ├─ Needs confirmation (reassignment, coating, thickness) → one prompt for the entry
│   │   ├─ Yes ──────────────────────────────────► staged; the confirmation is remembered
│   │   └─ No ───────────────────────────────────► reported "skipped: not confirmed"; NOT staged;
│   │                                               its earlier pending entry, if any, is KEPT
│   └─ Ok (with or without a warning) ───────────► staged
├─ Nothing staged? ──────────────────────────────► Error "<material> was not added to pending" + reasons.
└─ Something staged:
    ├─ Only the staged bodies are taken out of earlier pending entries (entries left empty are dropped)
    ├─ The new entry (staged bodies only) is added to Pending
    ├─ One info box for anything left out (already has it / blocked / declined) and any warnings
    ├─ Pending tab, Status column:  "Pending"  |  "Pending: <warning>"  |  "Pending — confirmed: <reason>"
    └─ Current Assignment tab: materials unchanged; Pending column flags the change
         body row  → "→ <new material [row]>"   (+ " (confirmed)" when a confirmation was given)
         group row → "<n> pending"
```

### B. Apply / OK

```
Apply / OK
├─ Pending not empty → apply the pending list:
│   ├─ Re-read the part
│   ├─ For each entry, in order:
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
│   ├─ Each success is recorded for Cancel's "revert changes?" list
│   ├─ One message: error if anything is still pending, info if everything applied but there were notes
│   └─ Both trees refresh: Current shows the new materials, Pending shows only what is left
│       ├─ Apply: the dialog stays open
│       └─ OK: anything still pending? → the dialog stays open, so you can see what did not apply
│                                     else → closes, keeping the changes
├─ Pending empty, sheet metal row picked, bodies selected → Add to pending + apply, as above
├─ Pending empty, sheet metal row picked, no solid body in the part → sets only the Sheet Metal Preferences
└─ Pending empty, nothing picked → Apply: error "Nothing is pending"; OK: closes
```

### C. Other actions

```
Remove from pending / Clear all pending ► removed from the list only; the part is not touched
Cancel / close ► pending entries are discarded (with a warning if there are any); anything applied while
                 the dialog was open is listed, with the choice to revert to the state the part was in
                 when the dialog opened
Refresh        ► re-reads the part and drops pending bodies that no longer exist (Apply re-plans the rest)
Remove material (Current tab) ► allowed while things are pending; Apply re-plans pending entries anyway
```

## Trees

Every tree (Material, Current Assignment, Pending) opens with its top-level rows expanded (level 1), and it is
expanded again after each rebuild.
