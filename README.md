# DataGlitchCore

Moteur de destruction de fichiers partagé par **DataBender** (audio) et **DataMosher**
(vidéo). Il ne connaît aucun format en particulier et n'a pas d'interface : chaque
application branche son propre parseur et construit son propre écran.

## Contenu

Tout est dans l'espace de noms `DataGlitchCore.Core`.

- `FileBuffer` : le fichier en mémoire, sous un plafond réglable, avec l'original
  intact pour comparer et revenir en arrière.
- `DestructionEngine` et `DestructionOptions` : une passe de destruction (mise à zéro,
  valeur fixe, XOR, rotation de bits, inversion, décalage, répétition de bloc),
  reproductible avec une seed fixée.
- `HeaderInfo` : où commence la zone destructible, fourni par le parseur du format.
- `DestructionMask` : les plages où une passe a le droit d'écrire.
- `OperationJournal` : chaque passe est un cran d'annulation et de rétablissement.
- `DiffMap` : la part d'octets modifiés, colonne par colonne.
- `ViewWindow` : la fenêtre visible, pour recalculer les rendus au zoom.
- `Riff` : le parcours des blocs d'un fichier RIFF (WAV, AVI).

## Installation

```bash
dotnet add package DataGlitchCore
```

## Développement

```bash
dotnet test
dotnet pack src/DataGlitchCore.Core -c Release -o artifacts   # paquet et symboles
```

## Publier une version

Le tag déclenche `.github/workflows/release.yml`. Il vérifie que le tag correspond à la
`<Version>` du projet, lance les tests, emballe le paquet et ses symboles, puis les
pousse sur nuget.org avec le secret `NUGET_API_KEY` du dépôt.

```bash
# 1. changer <Version> dans src/DataGlitchCore.Core/DataGlitchCore.Core.csproj
# 2. taguer : le workflow refuse un tag qui ne correspond pas à <Version>
git tag v0.1.0 && git push origin v0.1.0
```

## Licence

Apache 2.0.
