// ------------------------------------------------------------------
// DataGlitchCore — runtime de thème Scrap UI (window.scrapTheme)
//
// Chargé dans <head>, EN BLOQUANT (pas de defer, pas en fin de <body>) :
// l'attribut doit être posé sur <html> avant le tout premier rendu, sinon
// la page clignote en clair puis bascule.
//
// Même contrat que ScrapTheme dans @scrap-node/scrap-ui : le thème suit
// celui du système tant que rien n'est choisi, et un choix explicite est
// mémorisé sous la clé « scrap-theme ».
//
// Partagé par DataBender et DataMosher : ne rien mettre ici qui nomme
// l'un des deux softs en particulier.
// ------------------------------------------------------------------
window.scrapTheme = (function () {
    'use strict';

    const KEY = 'scrap-theme';
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const subs = new Set();
    let cache = new Map();
    let explicit = null; // 'light' | 'dark' | null (= suit l'OS)

    function read(k) {
        try { return localStorage.getItem(k); } catch (_) { return null; }
    }

    // Sonde de résolution des couleurs, créée au premier besoin : <body>
    // n'existe pas encore quand ce script s'exécute dans <head>.
    let probe = null;
    function resolve(name) {
        if (!probe) {
            probe = document.createElement('i');
            probe.setAttribute('aria-hidden', 'true');
            probe.style.cssText = 'position:absolute;width:0;height:0;overflow:hidden;visibility:hidden;pointer-events:none';
        }
        if (!probe.isConnected) (document.body || document.documentElement).appendChild(probe);
        probe.style.color = 'var(' + name + ')';
        return getComputedStyle(probe).color;
    }

    function apply(t) {
        document.documentElement.setAttribute('data-scrap-theme', t);
        cache = new Map(); // les tokens ont changé, tout le cache est périmé
        for (const fn of subs) {
            try { fn(t); } catch (_) { /* un abonné cassé ne doit pas casser les autres */ }
        }
    }

    const stored = read(KEY);
    explicit = stored === 'light' ? 'light' : stored === 'dark' ? 'dark' : null;
    document.documentElement.setAttribute(
        'data-scrap-theme', explicit ?? (mq.matches ? 'dark' : 'light'));

    // Tant que l'utilisateur n'a rien choisi, on suit l'OS À CHAUD : sans ça
    // la bascule système ne repeindrait pas les canvas (qui ne sont pas du CSS).
    mq.addEventListener('change', function (e) {
        if (!explicit) apply(e.matches ? 'dark' : 'light');
    });

    return {
        current() {
            return document.documentElement.getAttribute('data-scrap-theme');
        },

        toggle() {
            explicit = this.current() === 'dark' ? 'light' : 'dark';
            try { localStorage.setItem(KEY, explicit); } catch (_) { /* stockage indisponible : la bascule reste in-memory */ }
            apply(explicit);
        },

        // Couleur d'un token, résolue par le navigateur et mémoïsée ; le cache
        // est vidé à chaque bascule. Les jetons de la lib sont écrits en
        // light-dark(…) et en var(…) : lus tels quels par getComputedStyle,
        // ils reviendraient en texte brut, que ctx.fillStyle refuse en
        // silence. On les fait donc calculer par une sonde, dont la propriété
        // color revient toujours en rgb(…) ou rgba(…).
        color(name) {
            let v = cache.get(name);
            if (v === undefined) {
                v = resolve(name);
                cache.set(name, v);
            }
            return v;
        },

        // Triplet [r,g,b] d'un token. Mémoïsé sous une clé distincte.
        rgb(name) {
            const k = name + '#rgb';
            let v = cache.get(k);
            if (v === undefined) {
                const m = /rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/.exec(this.color(name));
                v = m ? [Math.round(+m[1]), Math.round(+m[2]), Math.round(+m[3])] : [128, 128, 128];
                cache.set(k, v);
            }
            return v;
        },

        rgba(name, a) {
            const c = this.rgb(name);
            return 'rgba(' + c[0] + ',' + c[1] + ',' + c[2] + ',' + a + ')';
        },

        // Interpolation linéaire entre deux tokens — pour les rampes canvas
        // (barres de diff, spectrogramme). t dans [0,1].
        lerp(a, b, t) {
            const x = this.rgb(a), y = this.rgb(b);
            return 'rgb(' +
                Math.round(x[0] + (y[0] - x[0]) * t) + ',' +
                Math.round(x[1] + (y[1] - x[1]) * t) + ',' +
                Math.round(x[2] + (y[2] - x[2]) * t) + ')';
        },

        // Abonnement : les canvas se redessinent à la bascule de thème.
        // Retourne la fonction de désabonnement.
        onChange(fn) {
            subs.add(fn);
            return () => subs.delete(fn);
        }
    };
})();
