using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Deplacement et camera premiere personne du joueur local.
/// Ce script ne s'execute que chez le proprietaire de l'avatar : les autres joueurs
/// sont de simples transforms pilotes par le NetworkTransform.
/// Tous les effets de camera sont purement locaux, rien de tout ca ne passe sur le reseau.
///
/// Principe directeur : le deplacement de base est fluide et nerveux. Aucune technique de
/// mouvement a maitriser (pas de propulsion, pas de crouch-jump, pas de roulade a l'arret) :
/// le jeu recompense le chaos involontaire, pas l'habilete au deplacement.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("Deplacement")]
    [SerializeField] private float walkSpeed = 4f;
    [SerializeField] private float sprintSpeed = 7f;
    [Tooltip("Volontairement faible : franchir une valise au sol, jamais grimper sur un meuble.")]
    [SerializeField] private float jumpHeight = 0.45f;
    [SerializeField] private float gravity = -20f;

    [Header("Inertie")]
    [Tooltip("Eleve = demarrage nerveux. On court beaucoup dans ce jeu, la base ne doit " +
             "jamais sembler lourde.")]
    [SerializeField] private float acceleration = 25f;
    [SerializeField] private float deceleration = 30f;

    [Header("Accroupissement")]
    [Tooltip("Se baisser, ralentir, etre discret. Aucune propulsion, aucun saut possible.")]
    [SerializeField] private float crouchSpeed = 1.8f;
    [SerializeField] private float crouchHeight = 1.2f;
    [SerializeField] private float crouchTransition = 9f;

    [Header("Camera")]
    [SerializeField] private Transform cameraPivot;      // pivot a hauteur des yeux
    [SerializeField] private float mouseSensitivity = 0.08f;
    [SerializeField] private float pitchMin = -80f;
    [SerializeField] private float pitchMax = 80f;
    [SerializeField] private float baseFov = 70f;

    [Tooltip("Amplitude du mouvement de tete quand le corps est soude a un chariot. Assez " +
             "pour jeter un oeil sur le cote, jamais assez pour voir derriere soi.")]
    [SerializeField] private float lookConeAngle = 55f;

    [Tooltip("Vitesse a laquelle la tete se remet dans l'axe une fois le chariot lache.")]
    [SerializeField] private float headRecenter = 220f;

    [Header("Balancement de marche")]
    [Tooltip("Amplitudes a 0 = effet desactive. A exposer dans les options : ce balancement " +
             "donne la nausee a une partie des joueurs.")]
    [SerializeField] private float bobFrequency = 9f;
    [SerializeField] private float bobVertical = 0.05f;
    [SerializeField] private float bobHorizontal = 0.035f;

    [Header("Impact et sprint")]
    [SerializeField] private float landingDip = 0.12f;
    [SerializeField] private float landingRecovery = 9f;
    [SerializeField] private float sprintFovBonus = 8f;
    [SerializeField] private float fovSmoothing = 6f;

    [Header("Roulade — EN VEILLE")]
    [Tooltip("A activer seulement quand le systeme porter/lacher fonctionnera en reseau : " +
             "la roulade renverse des objets physiques repliques, elle en depend.")]
    [SerializeField] private bool diveEnabled;
    [SerializeField] private float diveSpeed = 11f;
    [SerializeField] private float diveUpBoost = 2.5f;
    [SerializeField] private float diveDuration = 0.5f;
    [Tooltip("Fenetre de vulnerabilite : ni tourner, ni interagir. C'est la contrepartie.")]
    [SerializeField] private float recoveryDuration = 0.6f;
    [Range(0.05f, 1f)]
    [SerializeField] private float recoverySpeedFactor = 0.35f;
    [SerializeField] private float diveCameraDip = 0.35f;
    [SerializeField] private float diveCameraRoll = 14f;
    [SerializeField] private float divePushForce = 9f;

    [Header("Bousculade")]
    [Tooltip("Force de poussee, multipliee par la vitesse du joueur.")]
    [SerializeField] private float pushForce = 2.5f;
    [Tooltip("En dessous de cette vitesse on ne pousse rien : on ne deplace pas un chariot " +
             "en s'appuyant mollement dessus.")]
    [SerializeField] private float minPushSpeed = 1.2f;
    [Tooltip("Delai entre deux poussees du meme joueur. Sans lui, un contact prolonge " +
             "enverrait un message reseau par frame.")]
    [SerializeField] private float pushCooldown = 0.15f;
    [SerializeField] private float maxPushDistance = 3f;

    private CharacterController controller;
    private InputSystem_Actions input;
    private Camera playerCamera;
    private GameObject sceneCamera;                      // camera de la scene, eteinte le temps de la partie

    private float pitch;                                 // rotation verticale, camera uniquement
    private float headYaw;                               // tete tournee dans son cone, chariot en main
    private float verticalVelocity;
    private Vector3 horizontalVelocity;                  // lissee, c'est elle qui donne l'inertie
    private bool wasGrounded = true;

    private float crouchBlend;                           // 0 = debout, 1 = accroupi
    private float standingHeight;
    private float standingPivotY;

    private float nextPushTime;                          // anti-spam de la bousculade
    private float diveTimer;                             // > 0 : roulade en cours
    private float recoveryTimer;                         // > 0 : le perso se releve
    private Vector3 diveDirection;

    private Vector3 pivotBasePosition;                   // position de repos de la camera
    private float bobTimer;
    private float bobWeight;                             // 0 a l'arret, 1 en pleine marche
    private float landingOffset;

    private PlayerCarry carry;                           // ce que le joueur manipule

    private bool IsDiving => diveTimer > 0f;
    private bool IsCrouching => crouchBlend > 0.5f;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        carry = GetComponent<PlayerCarry>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Avatar d'un autre joueur : on coupe la vue et l'ecoute, mais on garde le pivot
            // actif. Il porte le point d'ancrage des objets tenus en main, qui doit rester
            // visible pour tout le monde.
            if (cameraPivot != null)
            {
                foreach (var cam in cameraPivot.GetComponentsInChildren<Camera>(true))
                    cam.enabled = false;

                foreach (var listener in cameraPivot.GetComponentsInChildren<AudioListener>(true))
                    listener.enabled = false;
            }

            enabled = false;
            return;
        }

        standingHeight = controller.height;

        // La camera de la scene sert avant la partie ; on l'eteint pour laisser la main a
        // celle du joueur, sinon deux cameras et deux AudioListener se marchent dessus.
        var main = Camera.main;
        if (main != null && !main.transform.IsChildOf(transform))
        {
            sceneCamera = main.gameObject;
            sceneCamera.SetActive(false);
        }

        if (cameraPivot != null)
        {
            pivotBasePosition = cameraPivot.localPosition;
            standingPivotY = pivotBasePosition.y;
            playerCamera = cameraPivot.GetComponentInChildren<Camera>();

            if (playerCamera != null)
                playerCamera.fieldOfView = baseFov;
        }

        input = new InputSystem_Actions();
        input.Player.Enable();

        SetCursorLocked(true);
    }

    public override void OnNetworkDespawn()
    {
        if (sceneCamera != null)
            sceneCamera.SetActive(true);

        ReleaseInput();
    }

    // override, pas une nouvelle methode : NetworkBehaviour.OnDestroy() desenregistre le
    // composant aupres de son NetworkObject. Le masquer laisse NGO avec des references
    // mortes, d'ou des NullReference a la fermeture.
    public override void OnDestroy()
    {
        ReleaseInput();
        base.OnDestroy();
    }

    private void ReleaseInput()
    {
        if (input == null) return;

        input.Player.Disable();
        input.Dispose();
        input = null;

        SetCursorLocked(false);
    }

    private void Update()
    {
        if (!IsOwner || input == null) return;

        HandleCursor();

        if (Cursor.lockState == CursorLockMode.Locked)
            Look();

        UpdateCrouch();
        Move();
        UpdateCameraFeel();
    }

    // ---------- Curseur ----------

    /// <summary>
    /// Echap libere le curseur (indispensable pour tester deux instances sur un seul ecran),
    /// un clic dans la fenetre le reprend.
    /// </summary>
    private void HandleCursor()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetCursorLocked(false);
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            SetCursorLocked(true);
    }

    private void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // ---------- Regard ----------

    private void Look()
    {
        // Pendant une roulade le joueur ne tourne plus : il subit sa trajectoire.
        if (IsDiving) return;

        var look = input.Player.Look.ReadValue<Vector2>() * mouseSensitivity;

        if (carry != null && carry.IsPushingCart)
        {
            // Corps soude au chariot : la souris ne fait plus tourner que la tete, dans un
            // cone. Assez pour jeter un oeil sur le cote, jamais assez pour voir derriere —
            // reculer se fait donc a l'aveugle, comme voulu.
            //
            // Ce mouvement n'est pas repliqué : les autres joueurs ne verront pas la tete
            // tourner, exactement comme pour le regard vertical. C'est le prix de ne pas
            // ajouter un axe sur le reseau.
            headYaw = Mathf.Clamp(headYaw + look.x, -lookConeAngle, lookConeAngle);
        }
        else
        {
            // Horizontal : on tourne tout le corps, donc c'est repliqué par le NetworkTransform.
            transform.Rotate(Vector3.up * look.x);
            headYaw = Mathf.MoveTowards(headYaw, 0f, headRecenter * Time.deltaTime);
        }

        // Vertical : uniquement la camera locale, un corps qui bascule serait absurde.
        pitch = Mathf.Clamp(pitch - look.y, pitchMin, pitchMax);
    }

    // ---------- Accroupissement ----------

    private void UpdateCrouch()
    {
        // Ni accroupi ni en roulade quand on tient une barre a deux mains.
        var wantsCrouch = input.Player.Crouch.IsPressed() && !IsDiving
                          && (carry == null || !carry.IsPushingCart);

        // On ne se releve pas dans un plafond bas : sinon on traverse le decor.
        if (!wantsCrouch && crouchBlend > 0.01f && BlockedAbove())
            wantsCrouch = true;

        crouchBlend = Mathf.MoveTowards(crouchBlend, wantsCrouch ? 1f : 0f,
                                       crouchTransition * Time.deltaTime);

        // L'origine du joueur est a ses pieds : le centre vaut donc toujours la moitie
        // de la hauteur.
        var height = Mathf.Lerp(standingHeight, crouchHeight, crouchBlend);
        controller.height = height;
        controller.center = new Vector3(0f, height * 0.5f, 0f);
    }

    private bool BlockedAbove()
    {
        var radius = controller.radius * 0.95f;
        var origin = transform.position + Vector3.up * (crouchHeight - controller.radius);
        var distance = Mathf.Max(standingHeight - crouchHeight, 0.01f);

        // SphereCastAll plutot que SphereCast : il faut ignorer notre propre capsule.
        foreach (var hit in Physics.SphereCastAll(origin, radius, Vector3.up, distance,
                                                  ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider != controller)
                return true;
        }

        return false;
    }

    // ---------- Deplacement ----------

    private void Move()
    {
        var grounded = controller.isGrounded;

        // Atterrissage : on mesure la vitesse de chute avant de la remettre a zero.
        if (grounded && !wasGrounded)
            landingOffset -= landingDip * Mathf.Clamp01(Mathf.Abs(verticalVelocity) / 12f);

        wasGrounded = grounded;

        // La conduite gere son propre deplacement et sa propre gravite : on sort ici, sinon
        // le deplacement normal s'ajouterait par-dessus et le joueur avancerait deux fois.
        if (carry != null && carry.TryGetCart(out var cart))
        {
            DriveCart(cart);
            return;
        }

        if (IsDiving)
            UpdateDive();
        else
            UpdateWalk(grounded);

        // Le saut et la roulade viennent peut-etre de fixer verticalVelocity : on ne la
        // remet a la valeur de collage au sol que si le perso descend effectivement.
        if (grounded && verticalVelocity < 0f)
            verticalVelocity = -2f;               // negatif plutot que zero : colle aux pentes
        else
            verticalVelocity += gravity * Time.deltaTime;

        controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
    }

    /// <summary>
    /// Le joueur conduit : ses touches pilotent le chariot, et sa propre position se deduit
    /// de celle du chariot. Il ne se deplace plus par lui-meme.
    ///
    /// C'est ce qui rend le blocage solidaire gratuit : le chariot seul decide de ce qui
    /// passe, donc un chariot arrete contre un mur arrete aussi son conducteur. Plus besoin
    /// de laisse ni de rappel — les deux ne peuvent pas diverger.
    /// </summary>
    private void DriveCart(Cart cart)
    {
        // Le sprint fonctionne aussi chariot en main : plus lent qu'a vide, mais il faut
        // pouvoir traverser un couloir sans que ce soit une punition.
        cart.SetDriveInput(input.Player.Move.ReadValue<Vector2>(),
                           input.Player.Sprint.IsPressed());

        var target = cart.OperatorPosition;
        var delta = target - transform.position;
        delta.y = 0f;

        // La gravite reste appliquee : le point d'ancrage est au sol du chariot, mais le
        // joueur doit coller au terrain et non leviter sur une bosse.
        verticalVelocity = controller.isGrounded
            ? -2f
            : verticalVelocity + gravity * Time.deltaTime;

        controller.Move(delta + Vector3.up * (verticalVelocity * Time.deltaTime));

        // Corps solidaire du chariot, orientation comprise. La tete, elle, garde un peu de
        // liberte — voir Look().
        transform.rotation = Quaternion.Euler(0f, cart.Yaw, 0f);

        // Renseignee pour tout ce qui lit la vitesse du joueur : balancement de marche,
        // atterrissage, champ de vision. Composante verticale ecartee, sinon une descente
        // gonflerait le balancement.
        var velocity = cart.Velocity;
        velocity.y = 0f;
        horizontalVelocity = velocity;
    }

    private void UpdateWalk(bool grounded)
    {
        if (recoveryTimer > 0f)
            recoveryTimer -= Time.deltaTime;

        var recovering = recoveryTimer > 0f;
        var move = input.Player.Move.ReadValue<Vector2>();

        // Pas de sprint accroupi, ni tant qu'on n'est pas releve.
        var sprinting = input.Player.Sprint.IsPressed() && !recovering && !IsCrouching;

        var uprightSpeed = sprinting ? sprintSpeed : walkSpeed;
        var speed = Mathf.Lerp(uprightSpeed, crouchSpeed, crouchBlend)
                    * (recovering ? recoverySpeedFactor : 1f);

        var direction = Vector3.ClampMagnitude(transform.right * move.x + transform.forward * move.y, 1f);
        var target = direction * speed;

        // MoveTowards plutot que Lerp : la montee en vitesse est lineaire et previsible.
        // Un Lerp donne cette sensation savonneuse des prototypes.
        var rate = target.sqrMagnitude > 0.01f ? acceleration : deceleration;
        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, target, rate * Time.deltaTime);

        if (!grounded || !input.Player.Jump.WasPressedThisFrame())
            return;

        // Pas de saut accroupi : c'est la porte d'entree de toutes les techniques de
        // mouvement, et on n'en veut aucune.
        if (crouchBlend > 0.1f || recovering)
            return;

        // Sprint lance + saut : on roule au lieu de sauter. Le joueur croit franchir
        // l'obstacle, il laboure le couloir. Desactive tant que la physique repliquee
        // n'est pas en place.
        if (diveEnabled && sprinting && horizontalVelocity.magnitude > walkSpeed * 0.8f)
            StartDive();
        else
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
    }

    // ---------- Roulade (en veille) ----------

    private void StartDive()
    {
        diveTimer = diveDuration;

        // Direction figee au depart : c'est ce qui retire le controle au joueur.
        diveDirection = horizontalVelocity.sqrMagnitude > 0.01f
            ? horizontalVelocity.normalized
            : transform.forward;

        verticalVelocity = diveUpBoost;
    }

    private void UpdateDive()
    {
        diveTimer -= Time.deltaTime;
        horizontalVelocity = diveDirection * diveSpeed;

        if (diveTimer <= 0f)
            recoveryTimer = recoveryDuration;
    }

    /// <summary>
    /// Bousculade des objets physiques. Seul celui qui marche detecte le contact — le
    /// serveur ne simule pas les CharacterController des joueurs distants — donc il constate
    /// et demande, il ne decide pas.
    /// </summary>
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!IsOwner || Time.time < nextPushTime) return;

        var body = hit.collider.attachedRigidbody;
        if (body == null) return;

        // Pas de test isKinematic ici : chez un client, NetworkRigidbody rend tous les
        // corps kinematic puisque seul le serveur simule. Ce test appartient au serveur.

        // Sans NetworkObject, l'objet n'existe que chez nous : le pousser creerait une
        // divergence entre les joueurs.
        var target = body.GetComponentInParent<NetworkObject>();
        if (target == null) return;

        // Jamais le chariot qu'on pousse : il est deja pilote par sa propre conduite, et une
        // bousculade par-dessus le ferait vibrer et saturerait le reseau a chaque contact.
        // Il nous bloque, en revanche — c'est ce qui rend un mur infranchissable avec lui.
        if (carry != null && carry.IsPushing(target)) return;

        var speed = horizontalVelocity.magnitude;
        if (speed < minPushSpeed && !IsDiving) return;

        nextPushTime = Time.time + pushCooldown;

        var direction = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
        if (direction.sqrMagnitude < 0.001f) return;

        RequestPushRpc(new NetworkObjectReference(target), direction.normalized, speed);
    }

    /// <summary>
    /// La poussee est appliquee par le serveur, sur sa seule simulation physique. C'est ce
    /// qui garantit que le chariot renverse finit au meme endroit pour tout le monde.
    /// </summary>
    [Rpc(SendTo.Server)]
    private void RequestPushRpc(NetworkObjectReference targetRef, Vector3 direction, float speed)
    {
        if (!targetRef.TryGet(out var target)) return;

        var body = target.GetComponent<Rigidbody>();
        if (body == null || body.isKinematic) return;

        // On ne pousse pas un objet situe a l'autre bout de l'hotel.
        if (Vector3.Distance(transform.position, target.transform.position) > maxPushDistance)
            return;

        // La vitesse annoncee par le client est bornee : au pire il pousse comme s'il
        // sprintait, jamais plus.
        var force = Mathf.Min(speed, sprintSpeed) * pushForce;

        if (IsDiving)
            force = divePushForce;

        body.AddForce(direction.normalized * force, ForceMode.Impulse);
    }

    // ---------- Ressenti de la camera ----------

    private void UpdateCameraFeel()
    {
        if (cameraPivot == null) return;

        var speedRatio = horizontalVelocity.magnitude / Mathf.Max(walkSpeed, 0.01f);
        var walking = controller.isGrounded && !IsDiving && speedRatio > 0.1f;

        // Appuye sur la barre d'un chariot, le buste est soutenu et la tete bouge moins.
        // Cela s'ajoute au ralentissement deja porte par speedRatio, la vitesse etant reduite.
        var bobFactor = carry != null ? carry.CameraBobFactor : 1f;

        // On fait varier l'amplitude plutot que de couper net le timer : sinon la camera
        // saute a chaque arret.
        bobWeight = Mathf.MoveTowards(bobWeight,
                                      walking ? Mathf.Min(speedRatio, 1.5f) * bobFactor : 0f,
                                      4f * Time.deltaTime);

        if (bobWeight > 0.001f)
            bobTimer += Time.deltaTime * bobFrequency * Mathf.Max(speedRatio, 0.5f);

        // Vertical a double frequence de l'horizontal : la trajectoire dessine un 8,
        // c'est ce qui imite la demarche plutot qu'un simple rebond.
        var bob = new Vector3(
            Mathf.Cos(bobTimer) * bobHorizontal * bobWeight,
            Mathf.Sin(bobTimer * 2f) * bobVertical * bobWeight,
            0f);

        // Courbe en cloche sur la duree de la roulade : la camera descend puis se redresse.
        var diveCurve = IsDiving
            ? Mathf.Sin((1f - diveTimer / diveDuration) * Mathf.PI)
            : 0f;

        landingOffset = Mathf.Lerp(landingOffset, 0f, landingRecovery * Time.deltaTime);

        // Les yeux suivent l'accroupissement, un peu moins que la capsule pour garder
        // la tete pres du haut du corps.
        var crouchedPivotY = standingPivotY - (standingHeight - crouchHeight) * 0.85f;
        var pivotY = Mathf.Lerp(standingPivotY, crouchedPivotY, crouchBlend);

        cameraPivot.localPosition = new Vector3(pivotBasePosition.x, pivotY, pivotBasePosition.z)
                                    + bob
                                    + Vector3.up * (landingOffset - diveCameraDip * diveCurve);

        // Le roulis n'existe que pendant la roulade : ailleurs il donne juste la nausee.
        cameraPivot.localRotation = Quaternion.Euler(pitch, headYaw, diveCameraRoll * diveCurve);

        if (playerCamera == null) return;

        var sprintingFov = input.Player.Sprint.IsPressed() && walking && !IsCrouching;
        var targetFov = baseFov + (sprintingFov || IsDiving ? sprintFovBonus : 0f);
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov,
                                              fovSmoothing * Time.deltaTime);
    }
}
