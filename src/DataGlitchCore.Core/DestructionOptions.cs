namespace DataGlitchCore.Core;

/// <summary>Ce qu'on fait aux octets d'un bloc ciblé.</summary>
public enum DestructionOperator
{
    /// <summary>Met les octets à zéro (silence/trous).</summary>
    Zero,
    /// <summary>Remplace par une valeur fixe (<see cref="DestructionOptions.FixedValue"/>).</summary>
    Fixed,
    /// <summary>XOR avec un masque (<see cref="DestructionOptions.XorMask"/>) — bruit contrôlé, réversible.</summary>
    Xor,
    /// <summary>Rotation de bits sur chaque octet (<see cref="DestructionOptions.BitRotate"/>).</summary>
    BitShift,
    /// <summary>Inverse l'ordre des octets du bloc.</summary>
    Reverse,
    /// <summary>Recopie des octets pris plus tôt dans le fichier (écho/traînée).</summary>
    ShiftBlock,
    /// <summary>Répète un petit motif du bloc (texture « accrochée », très glitch).</summary>
    DuplicateBlock,
}

/// <summary>
/// Paramètres d'une passe de destruction « orientée ». La zone, la densité, la
/// taille de bloc et la seed permettent d'éviter un random complet et de
/// reproduire exactement un rendu.
///
/// Volontairement sans rien de spécifique à un format : un ciblage propre à un
/// domaine (ex. bande de fréquences pour l'audio) se porte en dehors de ce type,
/// dans le projet qui connaît ce domaine — voir DataBender.Core.
/// </summary>
public sealed class DestructionOptions
{
    public DestructionOperator Operator { get; set; } = DestructionOperator.Xor;

    /// <summary>Probabilité qu'un bloc donné soit touché (0..1).</summary>
    public double Density { get; set; } = 0.15;

    /// <summary>Taille d'un bloc en octets (granularité de la destruction).</summary>
    public int BlockSize { get; set; } = 64;

    /// <summary>Seed du pseudo-aléatoire. null = non reproductible.</summary>
    public int? Seed { get; set; }

    /// <summary>Début de la zone ciblée, en fraction du buffer (0..1).</summary>
    public double RegionStart { get; set; } = 0.0;

    /// <summary>Fin de la zone ciblée, en fraction du buffer (0..1).</summary>
    public double RegionEnd { get; set; } = 1.0;

    /// <summary>Ne pas toucher le header détecté (garde le fichier lisible).</summary>
    public bool ProtectHeader { get; set; } = true;

    // --- paramètres spécifiques aux opérateurs ---
    public byte FixedValue { get; set; } = 0xFF;
    public byte XorMask { get; set; } = 0xFF;

    /// <summary>Nombre de bits de rotation (1..7) pour <see cref="DestructionOperator.BitShift"/>.</summary>
    public int BitRotate { get; set; } = 1;

    /// <summary>Décalage max (en blocs) pour <see cref="DestructionOperator.ShiftBlock"/>.</summary>
    public int MaxShiftBlocks { get; set; } = 8;

    public DestructionOptions Clone() => (DestructionOptions)MemberwiseClone();
}
