# Scrap UI, copie locale

`scrap-ui.css` est la feuille compilée de **@scrap-node/scrap-ui 0.3.0**
(`css/scrap-ui.css` dans le paquet) : jetons, base et composants, sans
framework. DataBender et DataMosher la chargent avant `app.css` et
`chrome.css`.

Elle est copiée plutôt qu'installée : les projets Data sont en .NET, sans
outillage npm. Ne pas la modifier ici : les surcharges propres aux outils
Data vont dans `app.css` (jetons) et `chrome.css` (coquille).

## Mettre à jour

Depuis un clone de ScrapUI, au tag de la version voulue :

```bash
git -C ../ScrapUI checkout v0.3.0 && npm --prefix ../ScrapUI run build
cp ../ScrapUI/dist/scrap-ui/css/scrap-ui.css src/DataGlitchCore.Ui/wwwroot/lib/scrap-ui/
```

ou depuis le registre (jeton `read:packages` requis) :

```bash
npm pack @scrap-node/scrap-ui@0.3.0 --registry=https://npm.pkg.github.com
tar -xzf scrap-node-scrap-ui-0.3.0.tgz package/css/scrap-ui.css
```

Puis reporter le numéro de version ci-dessus.
