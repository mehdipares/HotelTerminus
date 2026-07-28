using UnityEngine;

/// <summary>
/// Marque un objet comme outil de reparation : la cle a molette aujourd'hui.
///
/// Volontairement un simple marqueur, sans type ni categorie. Le jour ou un second outil
/// apparait — un tournevis pour l'electricite — on ajoutera un champ ici plutot que de creer
/// une seconde notion. Tant qu'il n'y a qu'un outil, en inventer la taxonomie serait du
/// travail pour rien.
///
/// A poser sur un objet portable : c'est en l'ayant en main qu'on s'en sert.
/// </summary>
[RequireComponent(typeof(Carryable))]
public class RepairTool : MonoBehaviour
{
}
