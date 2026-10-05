using BANxOpen.Materials.Contracts;
using BANxOpen.SheetMetal.Materials;

namespace BANxOpen.Ui.MaterialAssignment;

/// <summary>What a material tree row stands for: a physical material from the library, or — under a sheet metal
/// library — one of its rows in the sheet metal material standards file, which is what the part's Sheet Metal
/// Preferences get set to. A row always carries the physical material it is made of, since that is what is assigned
/// to the bodies.</summary>
public sealed record MaterialPick(Material Material, SheetMetalMaterialRow? Row = null)
{
    /// <summary>The row's name when there is one (e.g. <c>2024-O_0.016</c>), otherwise the material's.</summary>
    public string DisplayName => Row?.Name ?? Material.Name;
}
