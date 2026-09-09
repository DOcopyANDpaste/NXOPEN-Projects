using NxOpen.Foundation.Contracts.Materials;

namespace NxAdapters.Ui;

/// <summary>The full material-property popup, opened from the material tree. An interface rather than a
/// direct dependency so the presenter's logic does not drag in a second BlockDialog — and so the popup can
/// be left unwired (or swapped) without touching the presenter.</summary>
public interface IMaterialPropertyWindow
{
    void Show(Material material);
}
