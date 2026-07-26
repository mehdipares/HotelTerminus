# HotelTerminus — décisions techniques

Notes de référence sur les choix structurants du projet. À lire avant de modifier le réseau
ou la manipulation d'objets, pour ne pas défaire une décision volontaire.

---

## Réseau : qui décide de quoi

Le jeu est en **host-hébergé pair-à-pair** via le relais Steam (modèle Lethal Company). Pas
de serveur dédié : l'hôte est un joueur, sa machine simule la partie. Le relais et les
lobbies Steam sont gratuits et illimités, seul Steam Direct (100 $) coûte à la publication.

Conséquence à garder en tête : **tout ce que le serveur simule tourne sur le PC d'un
joueur.** C'est ce qui justifie de modérer le nombre d'objets physiques répliqués.

### Deux modes d'autorité, jamais mélangés

| Type d'objet | Autorité | Pourquoi |
|---|---|---|
| Avatar du joueur | **Owner** | chaque joueur pilote son perso, la réactivité prime ; la triche n'est pas un sujet en coop entre amis |
| Objets du monde (valises, chariots, cadavres) | **Serveur** | une seule simulation possible, sinon deux joueurs voient la valise à deux endroits |

Cela se traduit dans l'inspecteur par le champ `Authority Mode` du `NetworkTransform` :
`Owner` sur le prefab Player, `Server` sur les objets.

### NetworkRigidbody est obligatoire sur tout objet physique

`NetworkRigidbody` force le Rigidbody en **kinematic sur tous les clients non
autoritaires**. Seul le serveur simule réellement la chute, le résultat descend via le
`NetworkTransform`. Sans lui, chaque machine simulerait sa propre version et les objets
divergeraient.

**Piège qui en découle :** côté client, `rigidbody.isKinematic` est toujours `true`. Tester
`isKinematic` dans du code client pour savoir si un objet est manipulable **ne marche pas**.
Ce test appartient au serveur.

### Référencer un joueur : NetworkObject, jamais clientId

Pour désigner un joueur dans un état répliqué, on stocke une `NetworkObjectReference`, pas
un `clientId`.

`NetworkSpawnManager.GetPlayerNetworkObject(clientId)` **retourne null chez un client dès
qu'on lui demande l'avatar d'un autre joueur** — c'est une restriction volontaire de NGO en
mode client-serveur. Un objet porté référencé par `clientId` devient donc invisible pour
tout le monde sauf son porteur, alors que le code paraît correct et que le serveur, lui,
fonctionne.

Une `NetworkObjectReference` se résout chez tout le monde. Elle a un autre avantage : la
main d'un joueur et un réceptacle (douille, support) deviennent le même cas — les deux
exposent une ancre, l'objet attaché n'a pas à savoir lequel des deux le tient.

### Le client demande, le serveur décide

Aucune logique locale ne modifie un état visible par les autres. Le schéma est toujours :

```
client : intention (touche, collision)
  └─> ServerRpc
       └─> serveur : valide, applique, écrit une NetworkVariable
            └─> réplication à tous les clients
                 └─> chaque client en déduit son affichage
```

Les `NetworkVariable` du projet sont systématiquement en lecture pour tous, **écriture
serveur uniquement**.

---

## Conventions de prefab

### L'origine d'un personnage est à ses pieds

Le prefab `Player` est une racine vide à `0,0,0`, avec le modèle en enfant décalé vers le
haut. Le `CharacterController` respecte toujours `Center Y = Height / 2`.

Pourquoi : un joueur qu'on fait apparaître à `0,0,0` doit se retrouver au sol, sans
compensation à retenir. Une origine au milieu du corps oblige à corriger la hauteur partout
— spawns, téléportations, placement d'objets.

Le même découplage racine/modèle sert à corriger l'orientation d'un modèle importé (le robot
est tourné de -90° sur Y) sans toucher ni à la racine, ni au fichier source.

### Le point de préhension est un objet dédié

Chaque objet portable a un enfant vide `GripPoint`, placé là où la main doit le tenir : la
poignée d'une valise, la barre d'un chariot, les pieds d'un cadavre.

Pourquoi : l'origine d'un modèle importé est rarement au bon endroit — celle de
`Valise_Ranger` est à 1,33 m du sac. Attacher l'objet par son origine le ferait flotter à
côté de la main. Le `GripPoint` absorbe ce décalage **et** donne le contrôle de
l'orientation de l'objet une fois en main.

---

## Porter un objet

### Le parentage est répliqué, la position ne l'est pas

Quand le serveur valide un ramassage, il appelle `NetworkObject.TrySetParent(porteur)`. NGO
réplique la hiérarchie à tous les clients ; chacun aligne ensuite localement le `GripPoint`
de l'objet sur le `HandAnchor` du porteur.

L'alternative — le serveur repositionne l'objet à chaque frame — coûte de la bande passante
en continu et fait traîner l'objet derrière la main de celui qui le tient.

Le `NetworkTransform` de l'objet est **désactivé pendant le port** : sa position se déduit
du porteur, la répliquer en plus créerait un conflit.

### Le HandAnchor est sur la racine, pas sur la caméra

Il suit donc la rotation du corps (répliquée) mais **pas le regard vertical**.

Deux raisons : le pitch de la caméra n'est pas répliqué, donc un ancrage sous le pivot
caméra ferait voir l'objet à des hauteurs différentes selon les joueurs ; et visuellement,
une valise qui monte quand on regarde le plafond est immédiatement fausse.

### Porté ou posé : ce sont les colliders qui tranchent

Un objet ramassé voit **tous ses colliders désactivés**, pour qu'il ne percute pas son
porteur. Effet de bord exploité par la zone de livraison : un objet porté ne peut
physiquement pas déclencher un trigger. Traverser une chambre avec la valise en main ne
valide donc rien.

Le test `IsHeld` reste écrit explicitement dans `DeliveryZone` : si la gestion des colliders
change un jour, la règle métier doit rester lisible.

---

## Bousculer un objet

Le serveur ne simule pas les `CharacterController` des joueurs distants — leurs avatars sont
déplacés par le `NetworkTransform`, pas par `Move()`. Il ne peut donc **pas** détecter
lui-même qu'un joueur percute un objet.

C'est le seul cas du projet où le client constate un fait physique. Il envoie une requête, le
serveur applique la force sur sa simulation. Deux garde-fous : un délai entre deux poussées
(sinon un contact prolongé enverrait un message réseau par frame) et un plafond sur la
vitesse annoncée par le client.

---

## Kit de mouvement : ce qui est volontairement absent

Le déplacement de base doit rester **fluide et nerveux** — on court beaucoup dans ce jeu. Le
chaos vient d'une couche par-dessus, jamais de la lourdeur de base.

- **Saut faible** (`0.5 m`) : franchir une valise au sol, jamais grimper sur un meuble. Un
  sautillement de plateformer casserait le ton « employés d'hôtel ».
- **Ni saut ni sprint accroupi** : le crouch-jump est la porte d'entrée de toutes les
  techniques de mouvement. Le jeu doit récompenser le chaos involontaire, pas l'habileté au
  déplacement.
- **Aucune propulsion contrôlée.**
- **Roulade au sprint : écrite mais désactivée** (`Dive Enabled` décoché). Elle renverse des
  objets physiques répliqués, elle dépend donc du système porter/lâcher. À activer quand
  celui-ci sera consolidé.

Le balancement de caméra donne la nausée à une partie des joueurs : ses amplitudes sont
exposées séparément et devront apparaître dans les options.

---

## Provisoire, à retirer

- `TestItemSpawner` — fait apparaître une valise devant chaque joueur qui se connecte, pour
  tester sans rien placer à la main. À supprimer quand les objets seront posés dans le niveau.
- L'interface IMGUI de `SteamManager` (boutons Host / Join) — remplacée par un vrai menu.
- Le cube semi-transparent des `DeliveryZone`, en attendant le décor des chambres.

## Points d'accroche déjà en place

- `Carryable.ItemId` / `DeliveryZone.acceptedItemId` : vides aujourd'hui, ils permettront
  d'exiger la bonne valise dans la bonne chambre.
- `DeliveryZone.Delivered` (événement serveur) : le futur système de satisfaction client
  pourra noter une livraison sans modifier la zone.
- `PlayerCarry.HandSlot` : les RPC prennent déjà un slot de main, la seconde main n'obligera
  pas à changer le protocole réseau.

---

## Tester en local

Steam ne permet pas de tester à deux sur une seule machine : les deux instances partagent le
même compte, donc le même SteamId, et le relais ne sait pas router une connexion vers
soi-même.

Le `NetworkManager` porte donc **deux transports** : `FacepunchTransport` (Steam, pour la
production) et `UnityTransport` (`127.0.0.1`, pour le développement). On bascule via le champ
`Network Transport`. Le code de gameplay est identique dans les deux cas — `SteamManager`
détecte le transport actif et n'ouvre un lobby Steam que si c'est Facepunch.

Protocole de test : un build standalone en Host, l'éditeur en Join. **Run In Background** doit
être coché dans les Player Settings, sinon la fenêtre qui perd le focus cesse d'émettre et la
connexion tombe.
