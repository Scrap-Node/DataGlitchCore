namespace DataGlitchCore.Core;

/// <summary>
/// Ce que <see cref="DestructionEngine"/> a besoin de savoir sur la structure du
/// fichier pour ne pas corrompre ce qui doit rester lisible : où commence la zone
/// destructible. Un format concret (WAV, AVI…) construit ce type depuis son propre
/// parseur — <c>DataGlitchCore.Core</c> ne connaît aucun format en particulier.
/// </summary>
/// <param name="PayloadStart">
/// Premier octet destructible. La destruction ne descend jamais en dessous, ce qui
/// garde l'en-tête intact et le fichier lisible même après une passe agressive.
/// </param>
public sealed record HeaderInfo(int PayloadStart);
