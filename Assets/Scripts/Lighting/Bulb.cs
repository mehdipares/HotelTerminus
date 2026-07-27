using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Etat d'une ampoule : neuve ou grillee. Un seul prefab pour les deux etats.
///
/// Logique pure, aucun rendu : c'est <see cref="BulbVisual"/> qui traduit l'etat en
/// apparence. Cette separation compte pour la suite — a l'etape du vissage, c'est la
/// douille qui allumera la lumiere, pas l'ampoule.
///
/// Autorite serveur : seul le serveur ecrit l'etat.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Bulb : NetworkBehaviour
{
    [Tooltip("Etat au moment du spawn. Le serveur peut en changer ensuite via ServerSetBurnt.")]
    [SerializeField] private bool startBurnt;

    private readonly NetworkVariable<bool> isBurnt = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public bool IsBurnt => isBurnt.Value;

    /// <summary>
    /// Emis chez tous les clients a chaque changement, et une fois au spawn avec l'etat
    /// courant : un joueur qui rejoint en cours de partie doit voir les ampoules deja
    /// grillees, pas seulement celles qui grillent apres son arrivee.
    /// </summary>
    public event Action<bool> BurntChanged;

    public override void OnNetworkSpawn()
    {
        isBurnt.OnValueChanged += OnValueChanged;

        if (IsServer && startBurnt)
            isBurnt.Value = true;

        BurntChanged?.Invoke(isBurnt.Value);
    }

    public override void OnNetworkDespawn()
    {
        isBurnt.OnValueChanged -= OnValueChanged;
    }

    private void OnValueChanged(bool previous, bool current) => BurntChanged?.Invoke(current);

    /// <summary>Change l'etat de l'ampoule. Serveur uniquement.</summary>
    public void ServerSetBurnt(bool burnt)
    {
        if (!IsServer) return;

        isBurnt.Value = burnt;
    }
}
