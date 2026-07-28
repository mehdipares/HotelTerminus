using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Une roue de l'arrivee d'eau. On la manoeuvre en **maintenant E**, et elle garde sa
/// position — ouverte ou fermee.
///
/// L'eau ne coule que si **toutes** les roues du circuit sont ouvertes. A deux, chacun prend
/// la sienne et c'est vite fait ; seul, il faut les faire l'une apres l'autre. Le cout de
/// couper l'eau, c'est donc le temps passe a la cave — et c'est le monstre qui le rend cher,
/// pas un minuteur artificiel.
///
/// Reutilise tel quel le maintien du generateur : <see cref="IInteractable.IsHeldInteraction"/>,
/// la progression repliquee, le retrait d'un joueur qui s'eloigne ou se deconnecte.
///
/// Autorite serveur : lui seul tient la progression et ecrit l'etat.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WaterValve : NetworkBehaviour, IInteractable
{
    [Tooltip("Duree du maintien pour manoeuvrer cette roue.")]
    [SerializeField] private float turnDuration = 1.5f;

    [Tooltip("Vitesse a laquelle la manoeuvre se defait quand on relache, en multiple de la " +
             "vitesse de fermeture. La roue resiste : on ne la tourne pas par a-coups.")]
    [SerializeField] private float decayMultiplier = 2f;

    [Tooltip("Distance au-dela de laquelle le serveur retire un joueur de la manoeuvre.")]
    [SerializeField] private float holdMaxDistance = 4f;

    [Tooltip("Position au lancement de la partie.")]
    [SerializeField] private bool startOpen = true;

    private readonly NetworkVariable<bool> open = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> turnProgress = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Serveur uniquement : qui a les mains dessus en ce moment.
    private PlayerCarry holder;

    public bool IsOpen => open.Value;

    /// <summary>
    /// Emis chez tout le monde quand cette roue change de position. C'est le rendu qui
    /// s'y abonne — la logique ne dessine rien.
    /// </summary>
    public event Action<bool> OpenChanged;

    // ---------- Cycle reseau ----------

    public override void OnNetworkSpawn()
    {
        open.OnValueChanged += OnOpenChanged;

        if (IsServer)
        {
            open.Value = startOpen;
            WaterManager.ServerRegister(this);
        }

        // Un joueur qui rejoint doit voir la roue dans sa position reelle.
        OpenChanged?.Invoke(open.Value);
    }

    public override void OnNetworkDespawn()
    {
        open.OnValueChanged -= OnOpenChanged;

        if (IsServer)
            WaterManager.ServerUnregister(this);

        holder = null;
    }

    private void OnOpenChanged(bool previous, bool current) => OpenChanged?.Invoke(current);

    // ---------- IInteractable ----------

    public bool IsHeldInteraction => true;

    public float HoldProgress => turnProgress.Value;

    /// <summary>
    /// Manoeuvrable a tout moment, mains libres. Une roue d'arrivee d'eau n'a pas d'etat
    /// "hors service" : c'est le point du circuit qui doit toujours repondre, sinon un joueur
    /// peut se retrouver sans issue.
    /// </summary>
    public bool CanInteract(PlayerCarry player) => player != null && player.HasFreeHand;

    /// <summary>Vide : tout passe par le maintien.</summary>
    public void ServerInteract(PlayerCarry player) { }

    public void ServerHoldBegin(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        holder = player;
    }

    public void ServerHoldEnd(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        if (holder == player)
            holder = null;
    }

    // ---------- Boucle serveur ----------

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;

        PruneHolder();
        TickTurn();
    }

    /// <summary>
    /// Retire celui qui n'est plus en etat de manoeuvrer. Le serveur ne verifie que ce qu'il
    /// sait : la distance, qui vient des positions repliquees. Il ignore ou le joueur regarde
    /// — le pitch camera reste local — donc c'est le client qui signale qu'il ne vise plus.
    /// </summary>
    private void PruneHolder()
    {
        if (holder == null) return;

        if (!holder.IsSpawned
            || Vector3.Distance(holder.transform.position, transform.position) > holdMaxDistance)
        {
            holder = null;
        }
    }

    private void TickTurn()
    {
        var rate = 1f / Mathf.Max(0.1f, turnDuration);

        var delta = holder != null
            ? rate * Time.deltaTime
            : -rate * decayMultiplier * Time.deltaTime;

        var next = Mathf.Clamp01(turnProgress.Value + delta);

        if (!Mathf.Approximately(next, turnProgress.Value))
            turnProgress.Value = next;

        if (next < 1f) return;

        // Manoeuvre terminee : on bascule et on repart de zero, sans quoi la roue
        // s'inverserait en boucle tant que le joueur maintient.
        turnProgress.Value = 0f;
        holder = null;

        open.Value = !open.Value;

        // L'eau se recalcule a partir de TOUTES les roues : celle-ci ne decide pas seule.
        WaterManager.ServerRecompute();
    }
}
