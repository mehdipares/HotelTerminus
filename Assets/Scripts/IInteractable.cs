/// <summary>
/// Element du monde avec lequel un joueur peut interagir en le visant : ramasser une valise,
/// retirer une ampoule d'une douille, et plus tard ouvrir une porte, relancer le generateur,
/// consulter le registre de nuit.
///
/// A implementer sur un <see cref="Unity.Netcode.NetworkBehaviour"/> : le joueur doit pouvoir
/// designer la cible au serveur, ce qui suppose un NetworkObject.
///
/// <see cref="CanInteract"/> est evalue des deux cotes — chez le client pour colorer le point
/// de visee, chez le serveur pour valider la demande. Ce doit donc rester une simple lecture
/// d'etat, sans effet de bord.
/// <see cref="ServerInteract"/> ne s'execute que sur le serveur.
/// </summary>
public interface IInteractable
{
    /// <summary>L'action est-elle possible maintenant, pour ce joueur ?</summary>
    bool CanInteract(PlayerCarry player);

    /// <summary>Execute l'action. Serveur uniquement.</summary>
    void ServerInteract(PlayerCarry player);

    /// <summary>
    /// Action secondaire, au clic gauche : visser l'ampoule qu'on tient, actionner un
    /// levier, brancher un cable. Elle sert a *appliquer* ce qu'on a en main, la ou E sert
    /// a prendre et poser.
    ///
    /// Implementation par defaut vide : la plupart des objets n'ont pas d'action secondaire,
    /// ils n'ont donc rien a ecrire.
    /// </summary>
    bool CanUse(PlayerCarry player) => false;

    /// <summary>Execute l'action secondaire. Serveur uniquement.</summary>
    void ServerUse(PlayerCarry player) { }
}
