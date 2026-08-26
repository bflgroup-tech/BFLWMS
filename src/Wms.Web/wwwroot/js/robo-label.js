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
    let y = 0;

    // Header
    const hdr = Math.round(heightDots * 0.135);
    g.font = `900 ${hdr}px Arial, Helvetica, sans-serif`;
    g.textAlign = 'center';
    y += Math.round(heightDots * 0.155);
    g.fillText('BRANDS FOR LESS', widthDots / 2, y);

    // Description — English left, Arabic right. Both shrink to fit their half
    // rather than overrunning into each other.
    const desc = Math.round(heightDots * 0.082);
    y += Math.round(heightDots * 0.10);
    g.textAlign = 'left';
    fitText(g, f.descEn || '', pad, y, W * 0.56, desc, '700');
    g.textAlign = 'right';
    fitText(g, f.descAr || '', widthDots - pad, y, W * 0.40, Math.round(desc * 1.1), '700');

    // Hierarchy / item code line
    if (f.codeLine) {
        y += Math.round(heightDots * 0.085);
        g.textAlign = 'left';
        fitText(g, f.codeLine, pad, y, W, Math.round(heightDots * 0.075), '700');
    }

    // Barcode
    const ean = f.ean13 || '';
    if (ean.length === 13) {
        const mods  = eanModules(ean);
        const mw    = Math.max(1, Math.floor(W / mods.length));   // whole dots only
        const bw    = mw * mods.length;
        const bx    = Math.round((widthDots - bw) / 2);
        const bh    = Math.round(heightDots * 0.235);
        const by    = y + Math.round(heightDots * 0.03);
        for (let i = 0; i < mods.length; i++)
            if (mods[i] === '1') g.fillRect(bx + i * mw, by, mw, bh);
        y = by + bh;
    }

    // Human-readable digits
    if (ean) {
        y += Math.round(heightDots * 0.085);
        g.textAlign = 'left';
        g.font = `700 ${Math.round(heightDots * 0.082)}px Arial, Helvetica, sans-serif`;
        g.fillText(spaced(ean), pad, y);
    }

    // Footer: price | codes | Arabic price
    y = heightDots - Math.round(heightDots * 0.045);
    g.textAlign = 'left';
    g.font = `900 ${Math.round(heightDots * 0.125)}px Arial, Helvetica, sans-serif`;
    g.fillText(f.priceEn || '', pad, y);

    if (f.codesFoot) {
        g.textAlign = 'center';
        g.font = `700 ${Math.round(heightDots * 0.068)}px Arial, Helvetica, sans-serif`;
        g.fillText(f.codesFoot, widthDots / 2, y);
    }

    g.textAlign = 'right';
    g.font = `800 ${Math.round(heightDots * 0.105)}px Arial, Helvetica, sans-serif`;
    g.fillText(f.priceAr || '', widthDots - pad, y);

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
    const widthMm  = (opts && opts.widthMm)  || 100;
    const heightMm = (opts && opts.heightMm) || 50;

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
