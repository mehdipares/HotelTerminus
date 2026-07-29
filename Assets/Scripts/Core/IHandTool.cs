/// <summary>
/// Objet tenu en main dont on se sert **en continu** au clic gauche : l'extincteur
/// aujourd'hui, une perceuse ou un chalumeau demain.
///
/// Le clic gauche sert partout ailleurs a *appliquer d'un coup* ce qu'on tient — visser une
/// ampoule, poser un bagage sur un chariot. Un outil continu doit donc cohabiter avec ca, et
/// c'est la **distance** qui departage : colle a une cible qui l'accepte, on pose ; partout
/// ailleurs, on s'en sert.
/// </summary>
public interface IHandTool
{
    /// <summary>
    /// En train de servir. Tant que c'est vrai, l'objet ne peut etre ni lache ni pose —
    /// sinon un extincteur en pleine action atterrirait sur un chariot.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Distance au-dela de laquelle le clic gauche ne pose plus l'objet mais s'en sert.
    ///
    /// Volontairement courte. La portee d'interaction normale est de 2,5 m : sans cette
    /// reduction, vouloir arroser a deux metres d'un chariot y deposerait l'extincteur.
    /// </summary>
    float UseReach { get; }

    /// <summary>Secousse de camera a appliquer au porteur, de 0 a 1.</summary>
    float CameraShake { get; }

    /// <summary>
    /// Le porteur maintient ou relache le clic gauche. Appele **chez le proprietaire
    /// seulement** : a l'outil de faire suivre au serveur ce qui doit etre repliqué.
    /// </summary>
    void SetUsing(bool using_);
}
