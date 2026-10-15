using BANxOpen.Foundation.Contracts.Bodies;
using BANxOpen.Foundation.Contracts.Common;
using BANxOpen.Materials.Contracts;
using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.MaterialAssignment.SheetMetalSupport;

internal static class SheetMetalConflictFinder
{
    /// <summary>NX keeps one set of Sheet Metal Preferences per part, so every sheet metal body in it has to share
    /// a material and a preferences row — assigning a second one would re-point the preferences out from under the
    /// first. Returns why <paramref name="material"/> (as <paramref name="row"/>) cannot go onto
    /// <paramref name="targets"/>, or null when it can.
    ///
    /// Only the sheet metal bodies outside <paramref name="targets"/> count: the targets are about to take
    /// <paramref name="material"/>, so re-assigning every sheet metal body at once is not a conflict. A body
    /// claimed by a pending entry counts as that entry's material and row, since that is what it will have. A body
    /// already assigned counts as the part's current preferences row when that row is made of its material. Regular
    /// solids have no shared state, so a selection with no sheet metal body never conflicts.</summary>
    /// <param name="partRow">Reads the part's current preferences row. Called at most once, and only when there is
    /// another assigned sheet metal body to compare against: it is an NX query.</param>
    public static string? Find(
        Material material,
        SheetMetalMaterialRow? row,
        IReadOnlyList<BodyInfo> targets,
        IReadOnlyList<BodyInfo> allBodies,
        IReadOnlyDictionary<BodyId, BodyMaterialAssignment> currentAssignments,
        IReadOnlyList<PendingAssignmentEntry> pending,
        Func<SheetMetalMaterialRow?> partRow)
    {
        if (!targets.Any(b => b.Kind == BodyKind.SheetMetal))
            return null;

        var targetIds = targets.Select(b => b.Id).ToHashSet();

        var stagedEntries = new Dictionary<BodyId, PendingAssignmentEntry>();
        foreach (var entry in pending)
        {
            foreach (var pendingRow in entry.Rows)
                stagedEntries[pendingRow.Body.Id] = entry;
        }

        SheetMetalMaterialRow? currentPartRow = null;
        var partRowRead = false;

        var conflicting = new List<(BodyInfo Body, string Material, bool IsStaged)>();
        foreach (var body in allBodies.Where(b => b.Kind == BodyKind.SheetMetal && !targetIds.Contains(b.Id)))
        {
            string? otherMaterial;
            SheetMetalMaterialRow? otherRow;
            var isStaged = stagedEntries.TryGetValue(body.Id, out var staged);

            if (isStaged)
            {
                otherMaterial = staged!.Material.Name;
                otherRow = staged.SheetMetalRow;
            }
            else
            {
                otherMaterial = SheetMetalBodies.MaterialOf(body.Id, currentAssignments);
                if (otherMaterial is null)
                    continue;

                if (!partRowRead)
                {
                    currentPartRow = partRow();
                    partRowRead = true;
                }

                otherRow = currentPartRow is not null && SheetMetalBodies.SameName(currentPartRow.PhysicalMaterialName, otherMaterial)
                    ? currentPartRow
                    : null;
            }

            // An unknown row on either side cannot be told apart, so only the material is compared then.
            var sameMaterial = SheetMetalBodies.SameName(otherMaterial, material.Name);
            var sameRow = row is null || otherRow is null || SheetMetalBodies.SameName(otherRow.Name, row.Name);
            if (sameMaterial && sameRow)
                continue;

            conflicting.Add((body, otherRow is null ? otherMaterial : $"{otherMaterial} [{otherRow.Name}]", isStaged));
        }

        if (conflicting.Count == 0)
            return null;

        var name = row is null ? material.Name : $"{material.Name} [{row.Name}]";
        var details = conflicting.Select(x => $"  [{x.Body.Name}] {x.Material}{(x.IsStaged ? " (pending)" : string.Empty)}");
        return
            $"{name} cannot be assigned to sheet metal bodies in this part.{Environment.NewLine}{Environment.NewLine}" +
            $"Sheet Metal Preferences belong to the part, so every sheet metal body in it must have the same " +
            $"material and preferences row, and these already have a different one:{Environment.NewLine}" +
            string.Join(Environment.NewLine, details) +
            $"{Environment.NewLine}{Environment.NewLine}Nothing was assigned. Assign {name} to all of the " +
            "sheet metal bodies together, or leave the sheet metal bodies out of the selection.";
    }
}
