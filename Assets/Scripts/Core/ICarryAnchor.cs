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

    /// <summary>
    /// Ancre reservee a un objet precis. Une main ou une douille n'en ont qu'une et
    /// renvoient toujours la meme ; un plateau de chariot en a une par emplacement.
    ///
    /// Implementation par defaut : tout ce qui n'accueille qu'un objet n'a rien a ecrire.
    /// Meme procede que CanUse sur IInteractable.
    /// </summary>
    Transform AnchorFor(Carryable item) => Anchor;
}
