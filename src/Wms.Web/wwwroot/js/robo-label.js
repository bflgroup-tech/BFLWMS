// Robo Sorting price ticket -> Zebra ZD421 via Zebra Browser Print.
//
// WHY A BITMAP AND NOT ZPL TEXT
// ZPL's resident fonts carry no Arabic glyphs, and ZPL performs no Arabic
// contextual shaping — sending "طقم قميص وشورت" as ^FD prints disconnected
// letterforms at best. The browser already shapes Arabic correctly, so the whole
// label is drawn to a canvas and shipped as a single ^GFA graphic. Bars are drawn
// at exactly one canvas pixel per printer dot, so the barcode is as crisp as a
// native ^BC would be.
//
// WHY BROWSER PRINT AND NOT A SOCKET
// The app runs in Azure App Service; the printer is on the warehouse LAN. There is
// no network path from the server to the printer. Zebra Browser Print runs on the
// station and exposes a local HTTP API, which the page can reach because browsers
// treat http://127.0.0.1 as a trustworthy origin and therefore exempt it from
// mixed-content blocking on an HTTPS page.

const BP_HTTP  = 'http://127.0.0.1:9100';
const BP_HTTPS = 'https://127.0.0.1:9101';   // newer Browser Print builds

// ---------------------------------------------------------------- Browser Print

async function bpFetch(path, init) {
    // Try plain HTTP first (what most installs expose), then the TLS port.
    for (const base of [BP_HTTP, BP_HTTPS]) {
        try {
            const r = await fetch(base + path, init);
            if (r.ok) return r;
        } catch { /* try the next base */ }
    }
    return null;
}

async function getDefaultPrinter() {
    const r = await bpFetch('/default?type=printer', { method: 'GET' });
    if (!r) return null;
    const txt = (await r.text()).trim();
    if (!txt) return null;
    try { return JSON.parse(txt); } catch { return null; }
}

// ---------------------------------------------------------------- EAN-13

const EAN_L = ['0001101','0011001','0010011','0111101','0100011','0110001','0101111','0111011','0110111','0001011'];
const EAN_G = ['0100111','0110011','0011011','0100001','0011101','0111001','0000101','0010001','0001001','0010111'];
const EAN_R = ['1110010','1100110','1101100','1000010','1011100','1001110','1010000','1000100','1001000','1110100'];
const EAN_P = ['LLLLLL','LLGLGG','LLGGLG','LLGGGL','LGLLGG','LGGLLG','LGGGLL','LGLGLG','LGLGGL','LGGLGL'];

function eanModules(ean13) {
    const parity = EAN_P[+ean13[0]];
    let s = '101';
    for (let i = 0; i < 6; i++) {
        const d = +ean13[i + 1];
        s += parity[i] === 'L' ? EAN_L[d] : EAN_G[d];
    }
    s += '01010';
    for (let i = 0; i < 6; i++) s += EAN_R[+ean13[i + 7]];
    return s + '101';
}

// ---------------------------------------------------------------- canvas -> ZPL

// One canvas pixel per printer dot. Anything else reintroduces the scaling
// blur that made the browser-print version soft.
function renderLabel(f, widthDots, heightDots) {
    const c = document.createElement('canvas');
    c.width = widthDots; c.height = heightDots;
    const g = c.getContext('2d');

    g.fillStyle = '#fff'; g.fillRect(0, 0, c.width, c.height);
    g.fillStyle = '#000';
    g.textBaseline = 'alphabetic';

    const pad = Math.round(widthDots * 0.03);
    const W   = widthDots - pad * 2;
    const H   = heightDots;

    // Baselines and font sizes below are fractions of label height, measured off
    // the reference Brands For Less ticket (glyph advance widths against known
    // string lengths, not eyeballed) so the output holds against the original at
    // any stock size. Change these, not the call sites, to retune the layout.
    const L = {
        hdrFont:   0.155, hdrBase:  0.185,
        descFont:  0.080, descBase: 0.310,
        codeFont:  0.068, codeBase: 0.405,
        barTop:    0.455, barHeight: 0.190,
        digitFont: 0.078, digitBase: 0.720,
        footBase:  0.870, priceFont: 0.120, codesFont: 0.058, priceArFont: 0.105,
    };
    const px = frac => Math.round(H * frac);

    // Header — centred, heaviest weight on the label.
    g.textAlign = 'center';
    fitText(g, 'BRANDS FOR LESS', widthDots / 2, px(L.hdrBase), W, px(L.hdrFont), '900');

    // Description — English left, Arabic right. Each shrinks within its own half
    // so a long name can never run into the Arabic.
    g.textAlign = 'left';
    fitText(g, f.descEn || '', pad, px(L.descBase), W * 0.58, px(L.descFont), '700');
    g.textAlign = 'right';
    fitText(g, f.descAr || '', widthDots - pad, px(L.descBase), W * 0.38, px(L.descFont * 1.05), '700');

    // Hierarchy / item code line
    if (f.codeLine) {
        g.textAlign = 'left';
        fitText(g, f.codeLine, pad, px(L.codeBase), W, px(L.codeFont), '700');
    }

    // Barcode — full usable width, whole dots per module so no bar is a fraction
    // of a dot wide and every one prints the same weight.
    const ean = f.ean13 || '';
    if (ean.length === 13) {
        const mods = eanModules(ean);
        const mw   = Math.max(1, Math.floor(W / mods.length));
        const bw   = mw * mods.length;
        const bx   = Math.round((widthDots - bw) / 2);
        const by   = px(L.barTop);
        const bh   = px(L.barHeight);
        for (let i = 0; i < mods.length; i++)
            if (mods[i] === '1') g.fillRect(bx + i * mw, by, mw, bh);
    }

    // Human-readable digits, letter-spaced like the original.
    if (ean) {
        g.textAlign = 'left';
        fitText(g, spaced(ean), pad, px(L.digitBase), W, px(L.digitFont), '700');
    }

    // Footer: price | codes | Arabic price, all on one baseline.
    const fb = px(L.footBase);
    g.textAlign = 'left';
    fitText(g, f.priceEn || '', pad, fb, W * 0.32, px(L.priceFont), '900');

    if (f.codesFoot) {
        g.textAlign = 'center';
        fitText(g, f.codesFoot, widthDots / 2, fb, W * 0.40, px(L.codesFont), '700');
    }

    g.textAlign = 'right';
    fitText(g, f.priceAr || '', widthDots - pad, fb, W * 0.24, px(L.priceArFont), '800');

    return c;
}

// Shrinks a string until it fits maxW, so a long description never collides
// with the Arabic on the other side of the line.
function fitText(g, text, x, y, maxW, size, weight) {
    let s = size;
    do {
        g.font = `${weight} ${s}px Arial, Helvetica, sans-serif`;
        if (g.measureText(text).width <= maxW || s <= 8) break;
        s -= 1;
    } while (true);
    g.fillText(text, x, y);
}

function spaced(d) { return d.split('').join(' '); }

// 1-bit threshold, packed MSB-first, hex — the format ^GFA expects.
function canvasToZplGraphic(canvas) {
    const { width, height } = canvas;
    const px = canvas.getContext('2d').getImageData(0, 0, width, height).data;
    const rowBytes = Math.ceil(width / 8);
    const hex = [];

    for (let y = 0; y < height; y++) {
        let row = '';
        for (let b = 0; b < rowBytes; b++) {
            let byte = 0;
            for (let bit = 0; bit < 8; bit++) {
                const x = b * 8 + bit;
                if (x >= width) continue;
                const i = (y * width + x) * 4;
                // Luma threshold; alpha 0 counts as white.
                const dark = px[i + 3] > 128 &&
                             (px[i] * 0.299 + px[i + 1] * 0.587 + px[i + 2] * 0.114) < 128;
                if (dark) byte |= (0x80 >> bit);
            }
            row += byte.toString(16).padStart(2, '0').toUpperCase();
        }
        hex.push(row);
    }

    const data = hex.join('');
    const total = rowBytes * height;
    return { zpl: `^GFA,${total},${total},${rowBytes},${data}`, rowBytes, height };
}

// ---------------------------------------------------------------- entry point

// Returns a status string the caller surfaces to the operator. Never throws —
// a print failure must not lose the scan that produced the label.
export async function printLabel(fields, opts) {
    const dpi    = (opts && opts.dpi)    || 203;
    const widthMm  = (opts && opts.widthMm)  || 50;
    const heightMm = (opts && opts.heightMm) || 20;

    const widthDots  = Math.round(widthMm  / 25.4 * dpi);
    const heightDots = Math.round(heightMm / 25.4 * dpi);

    let canvas;
    try {
        canvas = renderLabel(fields, widthDots, heightDots);
    } catch (e) {
        return 'Label render failed: ' + (e && e.message ? e.message : e);
    }

    const printer = await getDefaultPrinter();
    if (!printer) {
        return 'NO_PRINTER';   // caller falls back to the browser print window
    }

    const gf = canvasToZplGraphic(canvas);
    const zpl =
        '^XA' +
        '^CI28' +                        // UTF-8, harmless for a pure-graphic label
        `^PW${widthDots}` +
        `^LL${heightDots}` +
        '^LH0,0' +
        '^FO0,0' + gf.zpl + '^FS' +
        '^XZ';

    const r = await bpFetch('/write', {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        body: JSON.stringify({ device: printer, data: zpl }),
    });

    if (!r) return 'Browser Print did not accept the job.';
    return 'OK:' + (printer.name || printer.uid || 'default printer');
}

// Diagnostic for the page's "Test printer" affordance — kept separate so a
// failure here reads as "no printer" rather than "label is wrong".
export async function probePrinter() {
    const p = await getDefaultPrinter();
    return p ? (p.name || p.uid || 'default printer') : '';
}

// ---------------------------------------------------------------- media probe

// One SGD round-trip: write the getvar, then read the reply back off the device.
async function sgd(printer, cmd) {
    await bpFetch('/write', {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        body: JSON.stringify({ device: printer, data: cmd }),
    });
    const r = await bpFetch('/read', {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain;charset=UTF-8' },
        body: JSON.stringify({ device: printer }),
    });
    if (!r) return '';
    return (await r.text()).replace(/"/g, '').trim();
}

// Asks the ZD421 what it is actually configured for, so the operator does not
// have to measure the stock. Returns "widthMm,heightMm,dpi" or "" if the printer
// will not answer — SGD reads are the flakiest part of Browser Print, so the
// caller must treat a blank as "type it in yourself", not as an error.
export async function readMedia() {
    const p = await getDefaultPrinter();
    if (!p) return '';
    try {
        // dots-per-mm -> dpi. ZD421 reports 8 (203dpi) or 12 (300dpi).
        const dpmm = parseFloat(await sgd(p, '! U1 getvar "internal_wired.printer.dpi"\r\n'))
                  || parseFloat(await sgd(p, '! U1 getvar "head.resolution.in_dpi"\r\n'));
        const wDots = parseFloat(await sgd(p, '! U1 getvar "ezpl.print_width"\r\n'));
        const lDots = parseFloat(await sgd(p, '! U1 getvar "zpl.label_length"\r\n'));

        const dpi = dpmm > 100 ? dpmm : (dpmm >= 11 ? 300 : 203);
        if (!(wDots > 0) || !(lDots > 0)) return '';
        const mm = d => Math.round(d / dpi * 25.4);
        return `${mm(wDots)},${mm(lDots)},${dpi}`;
    } catch {
        return '';
    }
}
