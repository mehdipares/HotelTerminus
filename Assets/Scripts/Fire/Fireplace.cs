using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Un foyer : un endroit qui peut prendre feu.
///
/// **Ne porte que l'etat**, jamais le rendu. Les flammes sont deduites par
/// <see cref="FireplaceVisual"/>, comme la lumiere d'une douille se deduit de l'ampoule et du
/// courant, et les particules d'un evier de sa fuite et de l'eau.
///
/// Autorite serveur : lui seul allume et eteint. Un client qui mettrait le feu de son cote
/// verrait une piece bruler que personne d'autre ne voit.
///
/// Volontairement minimal. L'extincteur, la propagation et le generateur en surchauffe
/// passeront tous par <see cref="ServerSetBurning"/> sans avoir a modifier cette classe.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Fireplace : NetworkBehaviour
{
    [Tooltip("Ce foyer brule-t-il des le lancement de la partie ? Utile pour tester.")]
    [SerializeField] private bool startBurning;

    private readonly NetworkVariable<bool> burning = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public bool IsBurning => IsSpawned && burning.Value;

    /// <summary>
    /// Emis chez tout le monde a chaque changement, **et une fois au spawn avec l'etat
    /// courant**. C'est cette seconde partie qui compte : un joueur qui rejoint doit voir les
    /// foyers deja en feu, pas seulement ceux qui s'allument apres son arrivee.
    /// </summary>
    public event Action<bool> BurningChanged;

    public override void OnNetworkSpawn()
    {
        burning.OnValueChanged += OnBurningChanged;

        if (IsServer && startBurning)
            burning.Value = true;

        BurningChanged?.Invoke(burning.Value);
    }

    public override void OnNetworkDespawn()
    {
        burning.OnValueChanged -= OnBurningChanged;
    }

    private void OnBurningChanged(bool previous, bool current) => BurningChanged?.Invoke(current);

    /// <summary>
    /// Allume ou eteint. Serveur uniquement — unique porte d'entree, qu'emprunteront
    /// l'extincteur, la propagation et tout ce qui mettra le feu plus tard.
    /// </summary>
    public void ServerSetBurning(bool value)
    {
        if (!IsServer || !IsSpawned || burning.Value == value) return;

        burning.Value = value;
        Debug.Log($"[Feu] {name} : {(value ? "en feu" : "eteint")}.");
    }

    /// <summary>
    /// Bascule le foyer. Provisoire, pour la touche de debug : meme pour du debug, le client
    /// demande et le serveur decide — on s'est deja fait avoir a court-circuiter l'autorite,
    /// ca donne des resultats de test faux.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleRpc()
    {
        ServerSetBurning(!burning.Value);
    }
}
