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

    /// <summary>
    /// L'action demande-t-elle de **maintenir** E au lieu d'appuyer ? Un generateur a
    /// relancer, une porte a forcer, un cadavre a trainer.
    ///
    /// Par defaut non : les objets deja ecrits gardent leur pression instantanee.
    /// </summary>
    bool IsHeldInteraction => false;

    /// <summary>
    /// Avancement 0..1 de l'action maintenue, pour l'affichage. Doit venir d'un etat
    /// repliqué : plusieurs joueurs peuvent pousser la meme action, ils doivent tous voir
    /// la meme jauge.
    /// </summary>
    float HoldProgress => 0f;

    /// <summary>Un joueur commence a maintenir. Serveur uniquement.</summary>
    void ServerHoldBegin(PlayerCarry player) { }

    /// <summary>
    /// Un joueur cesse de maintenir : touche relachee, regard detourne, trop loin, ou
    /// deconnexion. Serveur uniquement.
    /// </summary>
    void ServerHoldEnd(PlayerCarry player) { }
}
