using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Un foyer : un endroit qui brule, avec une **intensite** plutot qu'un simple etat allume ou
/// eteint.
///
/// **Ne porte que l'etat**, jamais le rendu. Les flammes sont deduites par
/// <see cref="FireplaceVisual"/>, comme la lumiere d'une douille se deduit de l'ampoule et du
/// courant, et les particules d'un evier de sa fuite et de l'eau.
///
/// **Il n'est plus interactif.** L'extinction etait un maintien de E abstrait : viser
/// vaguement le feu et attendre. Elle est desormais **spatiale** — c'est le jet de
/// l'extincteur qui, en touchant reellement le foyer, lui retire de l'intensite. Viser a cote
/// n'eteint rien.
///
/// Autorite serveur : lui seul ecrit l'intensite.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class Fireplace : NetworkBehaviour
{
    [Tooltip("Intensite d'un foyer a pleine puissance. L'echelle est arbitraire : c'est elle " +
             "qui donne son sens a la puissance d'extinction et a la reprise.")]
    [SerializeField] private float maxIntensity = 100f;

    [Tooltip("Ce foyer brule-t-il a pleine intensite des le lancement ? Utile pour tester.")]
    [SerializeField] private bool startBurning;

    [Header("Reprise")]
    [Tooltip("Intensite regagnee par seconde quand plus personne n'arrose. Un feu qu'on " +
             "laisse repart.")]
    [SerializeField] private float rekindlePerSecond = 6f;

    [Tooltip("Delai apres le dernier jet avant que le feu ne reparte. Sans lui, il " +
             "remonterait entre deux pas de physique et l'extinction n'avancerait jamais.")]
    [SerializeField] private float rekindleDelay = 1.2f;

    private readonly NetworkVariable<float> intensity = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private float lastSprayed;                   // serveur uniquement

    public float MaxIntensity => Mathf.Max(1f, maxIntensity);

    public float Intensity => IsSpawned ? intensity.Value : 0f;

    /// <summary>Intensite ramenee entre 0 et 1. C'est elle que lit le rendu.</summary>
    public float Normalized => Intensity / MaxIntensity;

    public bool IsBurning => Intensity > 0f;

    /// <summary>
    /// Emis chez tout le monde a chaque changement, **et une fois au spawn avec la valeur
    /// courante**. C'est cette seconde partie qui compte : un joueur qui rejoint doit voir les
    /// foyers deja en feu, pas seulement ceux qui s'allument apres son arrivee.
    /// </summary>
    public event Action<float> IntensityChanged;

    public override void OnNetworkSpawn()
    {
        intensity.OnValueChanged += OnIntensityChanged;

        if (IsServer && startBurning)
            intensity.Value = MaxIntensity;

        IntensityChanged?.Invoke(Normalized);
    }

    public override void OnNetworkDespawn()
    {
        intensity.OnValueChanged -= OnIntensityChanged;
    }

    private void OnIntensityChanged(float previous, float current)
        => IntensityChanged?.Invoke(Normalized);

    // ---------- Ecriture, serveur uniquement ----------

    /// <summary>
    /// Fixe l'intensite. Serveur uniquement — unique porte d'entree, qu'emprunteront la
    /// propagation et tout ce qui mettra le feu plus tard.
    /// </summary>
    public void ServerSetIntensity(float value)
    {
        if (!IsServer || !IsSpawned) return;

        var clamped = Mathf.Clamp(value, 0f, MaxIntensity);
        if (Mathf.Approximately(clamped, intensity.Value)) return;

        var wasBurning = intensity.Value > 0f;
        intensity.Value = clamped;

        if (wasBurning && clamped <= 0f)
            Debug.Log($"[Feu] {name} : eteint.");
    }

    /// <summary>Allume ou eteint completement. Conserve pour la touche de debug.</summary>
    public void ServerSetBurning(bool value) => ServerSetIntensity(value ? MaxIntensity : 0f);

    /// <summary>
    /// Le jet touche ce foyer. Appele par <see cref="Extinguisher"/>, cote serveur, a chaque
    /// pas de physique et avec la meme attenuation que la poussee : au coeur du jet ca eteint
    /// vite, en bordure a peine.
    /// </summary>
    public void ServerExtinguish(float amount)
    {
        if (!IsServer || !IsSpawned || amount <= 0f) return;

        lastSprayed = Time.time;
        ServerSetIntensity(intensity.Value - amount);
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (intensity.Value <= 0f || intensity.Value >= MaxIntensity) return;
        if (Time.time - lastSprayed < rekindleDelay) return;

        // Un feu qu'on laisse repart. C'est ce qui interdit de gratter quelques pourcents
        // avant d'aller faire autre chose.
        ServerSetIntensity(intensity.Value + rekindlePerSecond * Time.deltaTime);
    }

    /// <summary>
    /// Bascule le foyer. Provisoire, pour la touche de debug : meme pour du debug, le client
    /// demande et le serveur decide — on s'est deja fait avoir a court-circuiter l'autorite,
    /// ca donne des resultats de test faux.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleRpc()
    {
        ServerSetBurning(!IsBurning);
    }
}
