using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// TEMPORAIRE — outil de test uniquement.
///
/// F allume ou eteint le foyer vise. A retirer quand le feu aura de vraies causes :
/// generateur en surchauffe, plaque laissee allumee, sabotage.
///
/// Fait son propre rayon plutot que de passer par la visee de <see cref="PlayerCarry"/> :
/// celle-ci ne retient que les objets interactifs, et un foyer n'en est pas un — on ne
/// l'attrape pas, on l'eteint avec un extincteur. Un composant de debug isole ne doit de
/// toute facon rien imposer au code de production.
/// </summary>
public class FireDebugToggle : NetworkBehaviour
{
    [SerializeField] private bool enableKey = true;

    [Tooltip("Origine du rayon : le pivot camera.")]
    [SerializeField] private Transform aimSource;

    [SerializeField] private float reach = 6f;

    private void Update()
    {
        if (!enableKey || !IsOwner || !IsSpawned) return;
        if (Keyboard.current == null || !Keyboard.current.fKey.wasPressedThisFrame) return;

        var origin = aimSource != null ? aimSource : transform;

        // Triggers inclus : le collider d'un foyer en est un, pour qu'un feu ne soit pas un
        // bloc invisible dans lequel on se cogne.
        if (!Physics.Raycast(origin.position, origin.forward, out var hit, reach,
                             ~0, QueryTriggerInteraction.Collide))
        {
            return;
        }

        // GetComponentInParent : on touche le mesh, rarement la racine qui porte le composant.
        var fireplace = hit.collider.GetComponentInParent<Fireplace>();
        if (fireplace == null) return;

        // Le client demande, le serveur decide — meme pour du debug.
        fireplace.RequestToggleRpc();
    }
}
