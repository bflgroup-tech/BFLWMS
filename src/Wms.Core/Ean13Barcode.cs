using System.Text;

namespace Wms.Core;

/// <summary>
/// Minimal EAN-13 encoder that renders straight to inline SVG.
///
/// Inline SVG rather than an image or a barcode font: the print window is
/// generated client-side, so there is no asset to serve, nothing to install on
/// each sorting station, and the bars stay crisp at any print resolution.
///
/// EAN-13 specifically — the retail price ticket this backs is scanned at POS,
/// and the item codes on it are 13-digit GTINs. A 12-digit input is accepted and
/// the check digit computed; a 13-digit input has its check digit CORRECTED
/// rather than rejected, because a ticket that scans to the wrong GTIN is worse
/// than one carrying a corrected digit. Anything else returns null and the
/// caller prints the number without bars.
/// </summary>
public static class Ean13Barcode
{
    // Left-hand odd parity.
    private static readonly string[] L =
    {
        "0001101","0011001","0010011","0111101","0100011",
        "0110001","0101111","0111011","0110111","0001011",
    };

    // Left-hand even parity.
    private static readonly string[] G =
    {
        "0100111","0110011","0011011","0100001","0011101",
        "0111001","0000101","0010001","0001001","0010111",
    };

    // Right-hand (always even parity, always starts with a bar).
    private static readonly string[] R =
    {
        "1110010","1100110","1101100","1000010","1011100",
        "1001110","1010000","1000100","1001000","1110100",
    };

    // Which of L/G encodes each of the six left-hand digits, selected by the
    // first digit — that is how EAN-13 carries 13 digits in 12 positions.
    private static readonly string[] Parity =
    {
        "LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG",
        "LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL",
    };

    /// <summary>
    /// Digits-only form with a valid check digit, or null when the input cannot
    /// be an EAN-13 (wrong length, or contains non-digits).
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var sb = new StringBuilder(13);
        foreach (var ch in code)
            if (char.IsDigit(ch)) sb.Append(ch);

        var digits = sb.ToString();
        if (digits.Length == 13) digits = digits[..12];   // recompute the check digit
        if (digits.Length != 12) return null;

        return digits + CheckDigit(digits);
    }

    /// <summary>Modulo-10 check digit over the first 12 digits (weights 1,3,1,3…).</summary>
    private static char CheckDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>
    /// The 95-module bit pattern: left guard, six left digits, centre guard,
    /// six right digits, right guard.
    /// </summary>
    private static string Modules(string ean13)
    {
        var parity = Parity[ean13[0] - '0'];
        var sb = new StringBuilder(95);
        sb.Append("101");
        for (var i = 0; i < 6; i++)
        {
            var d = ean13[i + 1] - '0';
            sb.Append(parity[i] == 'L' ? L[d] : G[d]);
        }
        sb.Append("01010");
        for (var i = 0; i < 6; i++)
            sb.Append(R[ean13[i + 7] - '0']);
        sb.Append("101");
        return sb.ToString();
    }

    /// <summary>
    /// Inline SVG for the bars only — the human-readable number is laid out by
    /// the caller, matching the ticket format where it sits on its own line.
    /// Returns null when <paramref name="code"/> is not EAN-13-able.
    /// </summary>
    public static string? ToSvg(string? code, double moduleWidth = 2.2, double height = 70)
    {
        var ean = Normalize(code);
        if (ean is null) return null;

        var modules = Modules(ean);
        var width   = modules.Length * moduleWidth;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(width)}\" height=\"{F(height)}\" ")
          .Append($"viewBox=\"0 0 {F(width)} {F(height)}\" shape-rendering=\"crispEdges\">");

        // Emit one rect per run of set modules rather than per module — far fewer
        // nodes, and adjacent bars render without hairline seams.
        var i = 0;
        while (i < modules.Length)
        {
            if (modules[i] != '1') { i++; continue; }
            var start = i;
            while (i < modules.Length && modules[i] == '1') i++;
            sb.Append($"<rect x=\"{F(start * moduleWidth)}\" y=\"0\" ")
              .Append($"width=\"{F((i - start) * moduleWidth)}\" height=\"{F(height)}\" fill=\"#000\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
