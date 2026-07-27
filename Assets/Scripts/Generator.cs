using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Generateur de l'hotel : la seule facon de retablir le courant apres une panne.
///
/// Il se relance en **maintenant E**, et plusieurs joueurs peuvent s'y mettre ensemble —
/// c'est la premiere mecanique du jeu qui recompense le fait d'etre a deux. Une main seule
/// met <see cref="soloRepairSeconds"/>, chaque paire de bras en plus divise d'autant.
///
/// Autorite serveur : c'est lui qui tient la liste des joueurs en train de reparer et qui
/// fait avancer la jauge. La progression est repliquee, pas comptee chez chaque client :
/// deux joueurs qui reparent ensemble poussent la **meme** jauge, et chacun voit l'effort
/// de l'autre.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Generator : NetworkBehaviour, IInteractable
{
    [Header("Reparation")]
    [Tooltip("Duree de la remise en route par un joueur seul.")]
    [SerializeField] private float soloRepairSeconds = 9f;

    [Tooltip("Nombre de paires de bras reellement utiles. Au-dela, les joueurs en plus ne " +
             "servent a rien : la mecanique doit dire 'va chercher quelqu'un', pas " +
             "'attroupez-vous'.")]
    [SerializeField] private int maxHelpers = 2;

    [Tooltip("Vitesse a laquelle la jauge redescend quand plus personne ne repare, en " +
             "multiple de la vitesse de montee. Un doigt qui glisse ne punit pas, un " +
             "abandon oui.")]
    [SerializeField] private float decayMultiplier = 2f;

    [Tooltip("Distance au-dela de laquelle le serveur retire un joueur de la reparation.")]
    [SerializeField] private float holdMaxDistance = 4f;

    [Header("Etat")]
    [SerializeField] private bool startRunning = true;

    [Header("Debug — provisoire")]
    [Tooltip("U provoque la panne. A remplacer par les vraies causes : surcharge, nuit, " +
             "sabotage.")]
    [SerializeField] private bool debugBreakKey = true;

    private readonly NetworkVariable<bool> isRunning = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> repairProgress = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Serveur uniquement : qui a les mains dessus en ce moment.
    private readonly List<PlayerCarry> holders = new();

    public bool IsRunning => isRunning.Value;

    // ---------- IInteractable ----------

    public bool IsHeldInteraction => true;

    public float HoldProgress => repairProgress.Value;

    /// <summary>Reparable seulement s'il est en panne. Une fois relance, plus rien a faire.</summary>
    public bool CanInteract(PlayerCarry player) => !isRunning.Value && player != null;

    /// <summary>
    /// Vide volontairement : tout passe par le maintien. La presence de cette methode ne
    /// tient qu'a l'interface, qui sert aussi aux objets a pression instantanee.
    /// </summary>
    public void ServerInteract(PlayerCarry player) { }

    public void ServerHoldBegin(PlayerCarry player)
    {
        if (!IsServer || player == null || isRunning.Value) return;
        if (holders.Contains(player)) return;

        holders.Add(player);
    }

    public void ServerHoldEnd(PlayerCarry player)
    {
        if (!IsServer || player == null) return;

        holders.Remove(player);
    }

    // ---------- Cycle reseau ----------

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        isRunning.Value = startRunning;
        StartCoroutine(ApplyPowerNextFrame());
    }

    public override void OnNetworkDespawn()
    {
        holders.Clear();
    }

    private IEnumerator ApplyPowerNextFrame()
    {
        // Le generateur et le PowerManager sont deux objets poses dans la scene : rien ne
        // garantit lequel des deux spawne en premier. Une frame d'attente et tout le monde
        // est la.
        yield return null;

        ApplyPower();
    }

    // ---------- Boucle serveur ----------

    private void Update()
    {
        if (!IsSpawned) return;

        HandleDebugKey();

        if (!IsServer) return;

        PruneHolders();
        TickRepair();
    }

    /// <summary>
    /// Retire ceux qui ne sont plus en etat de reparer. Le serveur ne verifie que ce qu'il
    /// sait : la distance, qui vient des positions repliquees. Il ignore ou regarde le
    /// joueur — le pitch camera reste local — donc c'est le client qui signale qu'il ne
    /// vise plus.
    /// </summary>
    private void PruneHolders()
    {
        for (var i = holders.Count - 1; i >= 0; i--)
        {
            var player = holders[i];

            if (player == null || !player.IsSpawned ||
                Vector3.Distance(player.transform.position, transform.position) > holdMaxDistance)
            {
                holders.RemoveAt(i);
            }
        }
    }

    private void TickRepair()
    {
        if (isRunning.Value)
        {
            if (repairProgress.Value != 0f)
                repairProgress.Value = 0f;

            return;
        }

        var perHand = 1f / Mathf.Max(0.1f, soloRepairSeconds);

        // Les bras au-dela du plafond sont ignores : trois joueurs ne vont pas plus vite
        // que deux.
        var usefulHands = Mathf.Min(holders.Count, Mathf.Max(1, maxHelpers));

        var delta = holders.Count > 0
            ? usefulHands * perHand * Time.deltaTime
            : -perHand * decayMultiplier * Time.deltaTime;

        var next = Mathf.Clamp01(repairProgress.Value + delta);

        if (!Mathf.Approximately(next, repairProgress.Value))
            repairProgress.Value = next;

        if (next >= 1f)
            ServerSetRunning(true);
    }

    // ---------- Etat ----------

    /// <summary>Allume ou coupe le generateur. Serveur uniquement.</summary>
    public void ServerSetRunning(bool running)
    {
        if (!IsServer || !IsSpawned || isRunning.Value == running) return;

        isRunning.Value = running;
        repairProgress.Value = 0f;
        holders.Clear();

        Debug.Log($"[Generateur] {(running ? "relance" : "en panne")}.");
        ApplyPower();
    }

    /// <summary>
    /// Le generateur pilote le courant, mais les consommateurs ne le connaissent pas : ils
    /// lisent <see cref="PowerManager"/>. Le jour ou une deuxieme source apparait (groupe
    /// electrogene, batterie), rien ne change de leur cote.
    /// </summary>
    private void ApplyPower()
    {
        if (!IsServer) return;

        if (PowerManager.Instance == null)
        {
            Debug.LogWarning("[Generateur] Aucun PowerManager dans la scene.");
            return;
        }

        PowerManager.Instance.ServerSetPower(isRunning.Value);
    }

    private void HandleDebugKey()
    {
        if (!debugBreakKey || Keyboard.current == null) return;
        if (!Keyboard.current.uKey.wasPressedThisFrame) return;

        // Meme depuis un client : il demande, le serveur tranche. Une commande de test qui
        // court-circuiterait l'autorite donnerait des resultats de test faux.
        if (IsServer)
            ServerSetRunning(false);
        else
            RequestBreakRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestBreakRpc()
    {
        ServerSetRunning(false);
    }
}
