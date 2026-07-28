/// <summary>
/// Composant capable de **refuser** qu'on ramasse l'objet qu'il accompagne.
///
/// Un evier en grosse fuite ne se decroche pas : il faut d'abord couper l'eau et le reparer.
/// Sans ce refus, decrocher l'objet serait la facon la plus simple d'arreter une inondation,
/// et toute la boucle — descendre au sous-sol, fermer les deux roues, remonter reparer —
/// n'aurait plus de raison d'exister.
///
/// Volontairement generique : un extincteur plombe, une television vissee, un tableau
/// electrique sous tension pourront s'en servir sans que <see cref="Carryable"/> ait a les
/// connaitre.
///
/// Le joueur le constate au viseur, qui ne passe pas au vert.
/// </summary>
public interface IPickupBlocker
{
    bool BlocksPickup { get; }
}
