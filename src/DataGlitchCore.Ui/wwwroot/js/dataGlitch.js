// Squelette JS partagé par DataBender et DataMosher : délégation d'événements,
// fenêtre de vue temporelle (zoom/pan), calque de région, mini-carte, piste de
// diff, positionnement des têtes de lecture, export. Rien ici ne connaît un
// format de fichier en particulier (WAV, AVI…) ni un type de média (audio,
// vidéo) — c'est délibéré : chaque app étend cet objet global avec ses propres
// méthodes de rendu et de lecture (voir le commentaire "À FOURNIR" plus bas).
//
// Chargé AVANT le script propre à l'app : `window.dataGlitch` existe déjà quand
// l'app ajoute ses méthodes dessus (Object.assign ou affectations directes).
window.dataGlitch = {
    _last: null,
    _dotnet: null,
    _dragging: null,      // 'start' | 'end' pendant un drag

    // Tout est en fractions du FICHIER (0..1). L'affichage passe par _toView,
    // qui projette dans la fenêtre visible.
    _region: { start: 0, end: 1 },
    _view: { start: 0, end: 1 },
    _overview: null,
    _panning: false,
    _zoomBusy: false,
    _zoomPending: 1,
    _zoomAnchor: 0,

    _playPos: 0,          // tête de lecture partagée, en fraction [0,1]
    _dragEndedAt: 0,
    _active: 'orig',       // piste que pilotent la barre d'espace et le clic droit
    _loop: null,           // { start, end } en fractions, quand la boucle est active

    // ------------------------------------------------------------------
    // À FOURNIR par le script de chaque app (chargé après celui-ci) :
    //   - render(payload)            orchestre le rendu complet d'une passe
    //   - toggle(kind), swap()       play/pause et bascule A/B — appelés par
    //                                 init() ci-dessous (barre d'espace, clic droit)
    //   - reset()                    remet l'état de lecture à zéro
    // Aucune de ces trois méthodes n'existe sur ce squelette : les événements
    // qui les invoquent ne font rien tant qu'elles ne sont pas fournies.
    // ------------------------------------------------------------------

    // ---------- initialisation ----------

    init(dotnetRef) {
        this._dotnet = dotnetRef;
        if (this._hooked) return;
        this._hooked = true;

        // événements délégués : les éléments sont recréés à chaque rendu Blazor
        document.addEventListener('pointerdown', e => {
            const h = e.target.closest('[data-handle]');
            if (!h) return;
            e.preventDefault();
            this._dragging = h.dataset.handle;
            document.body.classList.add('dm-dragging');
        });

        window.addEventListener('pointermove', e => {
            if (!this._dragging) return;
            const pct = this._pctFromEvent(e);
            if (pct === null) return;
            this._region[this._dragging] = pct;
            this._applyRegion();
            this._notifyRegion(true);
        });

        window.addEventListener('pointerup', () => {
            if (!this._dragging) return;
            this._dragging = null;
            // _applyRegion place dmHandleStart au min et dmHandleEnd au max :
            // après un croisement, la poignée de gauche porte "end". Sans cette
            // remise en ordre, la saisie suivante de la poignée de droite
            // écrivait dans "start" et la borne gauche sautait d'un coup.
            const lo = Math.min(this._region.start, this._region.end);
            const hi = Math.max(this._region.start, this._region.end);
            this._region = { start: lo, end: hi };
            this._dragEndedAt = performance.now();
            document.body.classList.remove('dm-dragging');
            this._notifyRegion(false);
        });

        // clic sur un écran (hors barres de ciblage) = déplacer la tête de lecture
        document.addEventListener('click', e => {
            if (e.target.closest('[data-handle]')) return;
            // un relâchement de drag génère un click juste après : on l'ignore
            if (performance.now() - this._dragEndedAt < 250) return;
            const wrap = this._axisScreen(e);
            if (!wrap) return;
            const v = this._fractionIn(e, wrap);
            if (v === null) return;
            if (typeof this.seekFraction === 'function')
                this.seekFraction(this._toFile(v));
        });

        // molette sur un écran = zoom autour du curseur ; maj/horizontal = défilement
        document.addEventListener('wheel', e => {
            const screen = this._axisScreen(e);
            if (!screen || !this._dotnet) return;
            e.preventDefault();
            const v = this._fractionIn(e, screen);
            if (v === null) return;

            if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
                const amount = (e.shiftKey ? e.deltaY : e.deltaX) || e.deltaY;
                this._dotnet.invokeMethodAsync('OnPan', (amount / screen.clientWidth) * this._span());
                return;
            }
            const anchor = this._toFile(v);
            this._queueZoom(anchor, Math.pow(1.0016, -e.deltaY));
        }, { passive: false });

        // mini-carte : cliquer ou glisser déplace la fenêtre visible
        document.addEventListener('pointerdown', e => {
            const ov = e.target.closest('#dmOverview');
            if (!ov) return;
            e.preventDefault();
            this._panning = true;
            this._recentreFrom(e, ov);
        });
        window.addEventListener('pointermove', e => {
            if (!this._panning) return;
            const ov = document.getElementById('dmOverview');
            if (ov) this._recentreFrom(e, ov);
        });
        window.addEventListener('pointerup', () => { this._panning = false; });

        // clic droit sur la zone de visualisation = basculer entre original et détruit
        document.addEventListener('contextmenu', e => {
            if (!e.target.closest('.viz')) return;
            e.preventDefault();
            if (typeof this.swap === 'function') this.swap();
        });

        // barre d'espace = play/pause de la piste active
        document.addEventListener('keydown', e => {
            if (e.code !== 'Space' && e.key !== ' ') return;
            const t = e.target;
            // ne pas voler l'espace à une saisie de texte (champ seed, etc.)
            // ni à un entête de module, où il sert à replier/déplier
            if (t && (t.isContentEditable || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT'
                      || t.closest('.mod-head'))) return;
            if (t && t.tagName === 'INPUT' && t.type !== 'range' && t.type !== 'checkbox') return;
            if (typeof this.toggle !== 'function') return;
            // empêche le scroll de page et l'activation du bouton qui a le focus
            e.preventDefault();
            this.toggle(this._active);
        });

        let t;
        window.addEventListener('resize', () => {
            clearTimeout(t);
            t = setTimeout(() => { if (this._last && typeof this.render === 'function') this.render(this._last); }, 80);
        });
    },

    // ---------- géométrie pointeur ----------

    /// L'écran (.screen) sous le pointeur, s'il porte un axe temporel. Un écran
    /// qui n'en porte pas (ex. les aperçus image de DataMosher) se déclare avec
    /// data-seek="off" : sans ça, cliquer sur une image déplaçait la tête de
    /// lecture vers une frame sans rapport — et, dans DataMosher, basculait au
    /// passage la suppression d'une I-frame à l'insu de l'utilisateur.
    _axisScreen(e) {
        const s = e.target.closest('.screen');
        return s && s.dataset.seek !== 'off' ? s : null;
    },

    /// Position horizontale du pointeur, en fraction [0,1] de la zone DESSINÉE
    /// d'un élément : sa padding-box, celle où s'inscrivent les canvas et les
    /// repères en position:absolute — pas sa border-box (getBoundingClientRect),
    /// qui compte en plus la bordure de --scrap-border-w. Cet écart de 2 px
    /// décalait chaque clic de plusieurs frames sur une timeline dense
    /// (DataMosher : ~4 frames à 1800 frames sur 1000 px, de quoi ne jamais
    /// atteindre l'I-frame visée). null si l'élément n'a pas de largeur.
    _fractionIn(e, el) {
        const w = el.clientWidth;
        if (w <= 0) return null;
        const left = el.getBoundingClientRect().left + el.clientLeft;
        return Math.min(1, Math.max(0, (e.clientX - left) / w));
    },

    // ---------- fenêtre visible ----------

    _span() { return Math.max(1e-9, this._view.end - this._view.start); },

    /// fraction fichier → position [0,1] dans la fenêtre affichée
    _toView(f) { return (f - this._view.start) / this._span(); },

    /// position [0,1] dans la fenêtre → fraction fichier
    _toFile(v) { return this._view.start + v * this._span(); },

    /// Le zoom demande un recalcul serveur : on n'en garde qu'un en vol et on
    /// cumule les crans de molette arrivés entre-temps, sinon la file s'allonge
    /// plus vite qu'elle ne se vide et le zoom continue après l'arrêt du geste.
    _queueZoom(anchor, factor) {
        this._zoomAnchor = anchor;
        this._zoomPending *= factor;
        if (this._zoomBusy) return;
        this._flushZoom();
    },

    async _flushZoom() {
        this._zoomBusy = true;
        while (Math.abs(this._zoomPending - 1) > 1e-4) {
            const f = this._zoomPending;
            this._zoomPending = 1;
            try { await this._dotnet.invokeMethodAsync('OnZoom', this._zoomAnchor, f); }
            catch (_) { break; }
        }
        this._zoomPending = 1;
        this._zoomBusy = false;
    },

    _recentreFrom(e, ov) {
        if (!this._dotnet) return;
        const f = this._fractionIn(e, ov);
        if (f === null) return;
        this._dotnet.invokeMethodAsync('OnRecentre', f);
    },

    _pctFromEvent(e) {
        const overlay = document.getElementById('dmOverlay');
        if (!overlay) return null;
        const v = this._fractionIn(e, overlay);
        if (v === null) return null;
        return Math.min(1, Math.max(0, this._toFile(v)));
    },

    _notifyRegion(throttled) {
        if (!this._dotnet) return;
        const send = () => this._dotnet.invokeMethodAsync('OnRegionChanged', this._region.start, this._region.end);
        if (!throttled) { send(); return; }
        const now = performance.now();
        if (!this._lastNotify || now - this._lastNotify > 90) {
            this._lastNotify = now;
            send();
        }
    },

    // ---------- rendu générique ----------

    /// Blazor livre les byte[] en Uint8Array natif ; on garde le décodage base64
    /// en repli au cas où l'interop bascule sur du JSON. Utile à toute app qui
    /// reçoit un tableau d'octets depuis .NET (spectrogramme, vignettes…).
    _bytes(v) {
        if (ArrayBuffer.isView(v)) return v;
        if (Array.isArray(v)) return Uint8Array.from(v);
        const bin = atob(v);
        const out = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out;
    },

    /// Mini-carte : silhouette du fichier entier (si fournie via _overview, sinon
    /// ignorée), zone ciblée, fenêtre visible et tête de lecture — le seul repère
    /// qui reste quand on est zoomé très près.
    _drawOverview() {
        const p = this._prep('overviewCanvas');
        if (p && this._overview) {
            const { ctx, w, h } = p;
            const { min, max } = this._overview;
            const n = min.length, mid = h / 2;
            ctx.fillStyle = scrapTheme.rgba('--scrap-trace', 0.85);
            ctx.beginPath();
            ctx.moveTo(0, mid);
            for (let i = 0; i < n; i++) ctx.lineTo((i / n) * w, mid - max[i] * mid);
            for (let i = n - 1; i >= 0; i--) ctx.lineTo((i / n) * w, mid - min[i] * mid);
            ctx.closePath();
            ctx.fill();
        }

        const lo = Math.min(this._region.start, this._region.end);
        const hi = Math.max(this._region.start, this._region.end);
        const set = (id, l, wd) => {
            const el = document.getElementById(id);
            if (!el) return;
            el.style.left = (l * 100) + '%';
            if (wd !== undefined) el.style.width = (wd * 100) + '%';
        };
        set('dmOvRegion', lo, Math.max(0, hi - lo));
        set('dmOvWindow', this._view.start, this._span());
        set('dmOvHead', this._playPos);
    },

    /// Piste de diff : une colonne par fraction d'octets modifiés [0,1].
    _drawDiff(id, diff) {
        const p = this._prep(id);
        if (!p) return;
        const { ctx, w, h } = p;
        const n = diff.length;
        const bw = Math.max(1, w / n + 0.6);
        // Cuivre sourd quand c'est léger, cuivre franc quand c'est massif. Les
        // deux bornes sont des tokens et non une rampe codée en dur : le sens
        // de la rampe s'inverse selon le thème (sur papier, "plus de dégâts"
        // veut dire plus SOMBRE ; sur la plaque noire, plus CLAIR). Hissées
        // hors de la boucle : un getComputedStyle par colonne serait ruineux.
        const lo = scrapTheme.rgb('--scrap-diff-lo');
        const hi = scrapTheme.rgb('--scrap-diff-hi');
        for (let i = 0; i < n; i++) {
            const d = Math.min(1, Math.max(0, diff[i]));
            if (d <= 0) continue;
            const x = (i / n) * w;
            const bh = Math.max(2, d * h);
            const r = Math.round(lo[0] + (hi[0] - lo[0]) * d);
            const g = Math.round(lo[1] + (hi[1] - lo[1]) * d);
            const b = Math.round(lo[2] + (hi[2] - lo[2]) * d);
            ctx.fillStyle = `rgba(${r},${g},${b},${0.42 + 0.58 * d})`;
            ctx.fillRect(x, h - bh, bw, bh);
        }
    },

    _applyLoopMarkers(loop) {
        this._loop = loop || null;
        const el = document.getElementById('dmLoopZone');
        if (!el) return;
        if (!loop) { el.style.display = 'none'; return; }
        const a = Math.min(1, Math.max(0, this._toView(loop.start)));
        const b = Math.min(1, Math.max(0, this._toView(loop.end)));
        // entièrement hors de la fenêtre : rien à montrer
        if (b <= 0 || a >= 1 || b - a <= 0) { el.style.display = 'none'; return; }
        el.style.display = 'block';
        el.style.left = (a * 100) + '%';
        el.style.width = ((b - a) * 100) + '%';
    },

    _applyRegion() {
        const lo = this._toView(Math.min(this._region.start, this._region.end));
        const hi = this._toView(Math.max(this._region.start, this._region.end));

        // une poignée hors de la fenêtre n'est pas saisissable : on la cache plutôt
        // que de la coller au bord, où elle mentirait sur la position réelle
        const handle = (id, v) => {
            const el = document.getElementById(id);
            if (!el) return;
            const out = v < -0.001 || v > 1.001;
            el.style.display = out ? 'none' : 'block';
            if (!out) el.style.left = (v * 100) + '%';
        };
        handle('dmHandleStart', lo);
        handle('dmHandleEnd', hi);

        const clamp = v => Math.min(1, Math.max(0, v));
        const set = (id, prop, val) => { const el = document.getElementById(id); if (el) el.style[prop] = val; };
        set('dmShadeL', 'width', (clamp(lo) * 100) + '%');
        set('dmShadeR', 'left', (clamp(hi) * 100) + '%');
        set('dmShadeR', 'width', ((1 - clamp(hi)) * 100) + '%');
    },

    /// Prépare un <canvas> pour un tracé net (dimensionnement DPR-aware), et le
    /// vide. Renvoie null si l'élément n'existe pas encore dans le DOM.
    _prep(id) {
        const canvas = document.getElementById(id);
        if (!canvas) return null;
        const dpr = window.devicePixelRatio || 1;
        const rect = canvas.getBoundingClientRect();
        const w = Math.max(1, Math.floor(rect.width));
        const h = Math.max(1, Math.floor(rect.height));
        canvas.width = w * dpr;
        canvas.height = h * dpr;
        const ctx = canvas.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);
        return { ctx, w, h };
    },

    // ---------- tête(s) de lecture ----------

    _setPlayhead(kind, frac) {
        const el = document.getElementById(kind + 'Playhead');
        if (!el) return;
        const v = this._toView(frac);
        // zoomé, la position sort vite du cadre : mieux vaut pas de trait qu'un
        // trait bloqué sur le bord, qui laisserait croire qu'on y est
        if (v < 0 || v > 1) { el.style.display = 'none'; return; }
        el.style.display = 'block';
        el.style.left = (v * 100) + '%';
    },

    /// Positionne toutes les têtes de lecture nommées, plus le repère de la
    /// mini-carte. `kinds` par défaut couvre le cas à deux pistes (orig/dest) ;
    /// une app avec d'autres pistes (ex. un spectrogramme) passe sa propre liste.
    _renderPlayheads(kinds = ['orig', 'dest']) {
        for (const k of kinds) this._setPlayhead(k, this._playPos);
        const head = document.getElementById('dmOvHead');
        if (head) head.style.left = (this._playPos * 100) + '%';
    },

    // ---------- export ----------

    async download(name, streamRef) {
        const buffer = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer]));
        const a = document.createElement('a');
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    }
};

// Le CSS bascule en silence (basculement système, ou bouton .theme-toggle) :
// sans cet abonnement, les canvas garderaient l'ancienne palette jusqu'au
// prochain redimensionnement. Même garde qu'au redimensionnement (l.143).
scrapTheme.onChange(() => {
    if (window.dataGlitch._last && typeof window.dataGlitch.render === 'function') {
        window.dataGlitch.render(window.dataGlitch._last);
    }
});
