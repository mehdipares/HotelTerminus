using UnityEngine;

/// <summary>
/// Point ou un <see cref="Carryable"/> vient se coller : la main d'un joueur, une douille,
/// un support mural, un tableau a cles.
///
/// Permet au Carryable d'ignorer completement ce qui le tient — il aligne son point de
/// prehension sur cette ancre, sans savoir s'il est dans une main ou visse quelque part.
/// </summary>
public interface ICarryAnchor
{
    Transform Anchor { get; }
}
