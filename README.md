# DataGlitchCore

Moteur de destruction de fichiers partagé par **DataBender** (audio) et **DataMosher**
(vidéo). Il ne connaît aucun format en particulier et n'a pas d'interface : chaque
application branche son propre parseur et construit son propre écran.

## Contenu

Tout est dans l'espace de noms `DataGlitchCore.Core`.

- `FileBuffer` : le fichier, jamais tenu en entier en mémoire
  ([#7](https://github.com/Scrap-Node/DataGlitchCore/issues/7)). L'original reste sur
  disque, en lecture seule (`FileBuffer.Open`). L'état courant n'existe pas en entier :
  c'est l'original plus une pile de segments, un par passe, écrits dans le dossier de la
  session. Le processus ne garde que l'index et les compteurs de chaque segment (12 octets
  par Mio) et la passe en construction (32 Mio au plus). `FileBuffer.FromBytes` garde tout
  en mémoire, pour les tests.
- `ByteView` : un état du fichier (`Original`, `Current`, l'avant et l'après d'une passe),
  lu par fenêtres ou en flux positionnable (`OpenRead`). Une lecture recompose l'état à
  partir de l'original et des segments qui touchent la plage.
- `SourceFile` : la copie d'un upload dans la session, coupée à une taille maximale, avec
  son SHA-256.
- `DestructionEngine` et `DestructionOptions` : une passe de destruction (mise à zéro,
  valeur fixe, XOR, rotation de bits, inversion, décalage, répétition de bloc),
  reproductible avec une seed fixée. Elle s'écrit dans un segment, sans rien changer tant
  qu'elle n'est pas appliquée.
- `PassBuilder` : une passe construite à la main, à partir d'un avant et d'un après
  (`WriteDiff`, tranche par tranche), pour reprendre le résultat d'une passe.
- `HeaderInfo` : où commence la zone destructible, fourni par le parseur du format.
- `DestructionMask` : les plages où une passe a le droit d'écrire.
- `OperationJournal` : chaque passe est un cran d'annulation et de rétablissement, qui ne
  réécrit aucun octet : annuler déplace le sommet de la pile de segments. Il numérote les
  passes (`PassInfo.Id`), range avec chacune une donnée de l'app (`AppData`), et compte
  exactement les octets qui diffèrent de l'original (`ChangedBytes`). L'historique est
  borné (`JournalOptions`) : au-delà de 20 passes, ou du budget disque de la session, les
  plus anciennes fusionnent dans une base et ne s'annulent plus une à une
  (`MergedPasses`, `MergedAppData`). La base devient une copie complète de l'état dès
  qu'elle couvre la moitié du fichier : une session ne prend jamais plus de deux fois le
  fichier, plus ses passes annulables. Une passe qui ne tiendrait pas dans le budget est
  refusée avant d'être construite.
- `SequenceScript` : le montage d'une suite d'unités numérotées (les frames d'une vidéo,
  des blocs d'échantillons) : supprimer, répéter, coller une plage ailleurs, avec
  annuler et rétablir. Son plan se déroule en une sortie (`SequenceOutput`) qui dit,
  pour chaque unité, d'où elle vient et à quelle position de l'original elle joue.
  Le format réécrit le fichier à partir de cette sortie.
- `DiffMap` : la part d'octets modifiés, colonne par colonne. Sur un buffer, il lit les
  compteurs du journal dès qu'une colonne dépasse 4 Mio.
- `ViewWindow` : la fenêtre visible, pour recalculer les rendus au zoom.
- `Riff` : le parcours des blocs d'un fichier RIFF (WAV, AVI), sur un `ByteView` : seuls
  les en-têtes sont lus.
- `ExportFile` : l'écriture d'un export, tout ou rien. Le fichier s'écrit à côté de sa
  cible sous un nom temporaire, puis la remplace d'un coup : une erreur ou une
  annulation ne laisse ni fichier à moitié écrit, ni fichier existant écrasé. Le
  temporaire est forcé sur le disque avant le renommage, et le dossier après
  (`F_FULLFSYNC` sous macOS ; sous Windows, le renommage s'écrit directement sur le
  disque) : une coupure de courant ne défait pas un export terminé.

## Installation

```bash
dotnet add package DataGlitchCore
```

## Développement

```bash
dotnet test
dotnet pack src/DataGlitchCore.Core -c Release -o artifacts   # paquet et symboles
```

Les tests comprennent un oracle (`OracleTests`) : le moteur de la 0.2.0, recopié sous
`tests/…/Reference`, sert de référence aux « mêmes octets à seed égale ». Des suites
aléatoires de passes, d'annulations et de retours à l'original y comparent le cœur à
chaque pas, en mémoire comme sur disque, avec des tranches et des tampons réduits à
quelques Kio pour forcer tous les chemins. Une suite qui échoue donne sa seed :

```bash
DGC_ORACLE_SEED=1234 dotnet test --filter Oracle   # rejouer une suite
DGC_ORACLE_SEEDS=5000 dotnet test --filter Oracle  # en lancer davantage (150 par défaut)
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

Apache 2.0, voir les fichiers `LICENSE` et `NOTICE`, joints aussi au paquet NuGet. La
licence porte sur le code : elle ne cède ni le nom « Scrap », ni les logos, ni les autres
signes distinctifs des projets Scrap Node.
