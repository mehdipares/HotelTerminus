/// <summary>
/// Objet qui doit etre **en place** pour fonctionner : un evier accroche au mur, une machine
/// a cafe posee sur son plan, une television fixee.
///
/// Un objet encore dans les mains ou pose par terre n'est pas en service — il ne fuit pas,
/// ne chauffe pas, ne s'allume pas. C'est le bon sens, et sans ce contrat un evier se mettait
/// a couler alors qu'on le portait.
///
/// Les comportements interrogent cette interface avec un defaut permissif : un composant qui
/// n'en porte pas est considere en service. Rien de ce qui existe n'a donc a changer.
/// </summary>
public interface IInstallable
{
    bool IsInstalled { get; }

    /// <summary>
    /// Emis chez tout le monde quand l'objet est mis en place ou retire. Le rendu s'y abonne
    /// pour couper ses effets des qu'on decroche l'objet, sans attendre un autre evenement.
    /// </summary>
    event System.Action<bool> InstalledChanged;
}
