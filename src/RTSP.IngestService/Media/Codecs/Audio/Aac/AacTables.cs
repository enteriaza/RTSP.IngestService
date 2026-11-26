// Constant tables of the AAC-LC decoder: scalefactor band offsets per sampling frequency (ISO/IEC 14496-3, Tables
// 4.129 to 4.147), TNS maximum bands (Table 4.156), the spectral codebooks and the inverse-quantisation and gain
// tables. Band offsets were transcribed from sfBandTabLong/sfBandTabShort in vo-aacenc aac_rom.c (Apache License
// 2.0, Copyright 2003-2010 VisualOn, Inc.), which reproduce the ISO tables; 7350 Hz uses the 8000 Hz tables as the
// specification requires. See Media/Codecs/THIRD-PARTY-NOTICES.md.

using RTSP.IngestService.Media.Codecs.Common.Entropy;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac;

/// <summary>AAC-LC decoder tables.</summary>
internal static class AacTables
{
    /// <summary>Spectral coefficients per long window.</summary>
    public const int LongWindowLength = 1024;

    /// <summary>Spectral coefficients per short window.</summary>
    public const int ShortWindowLength = 128;

    /// <summary>Largest quantised magnitude the inverse quantiser accepts: escape values plus four pulses of 15.</summary>
    public const int MaxQuantisedMagnitude = 8191 + (4 * 15);

    /// <summary>Sampling frequencies by index.</summary>
    public static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Long-window TNS maximum bands by sampling frequency index (AAC LC).</summary>
    public static readonly int[] TnsMaxBandsLong = [31, 31, 34, 40, 42, 51, 46, 46, 42, 42, 42, 39, 39];

    /// <summary>Short-window TNS maximum bands by sampling frequency index (AAC LC).</summary>
    public static readonly int[] TnsMaxBandsShort = [9, 9, 10, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14];

    /// <summary>|q|^(4/3) for 0 to <see cref="MaxQuantisedMagnitude"/>.</summary>
    public static readonly float[] InverseQuantisation = CreateInverseQuantisation();

    /// <summary>2^((sf - 100) / 4) for scalefactors 0 to 255.</summary>
    public static readonly float[] ScaleFactorGain = CreateScaleFactorGain();

    /// <summary>The scalefactor codebook (symbol = difference + 60).</summary>
    public static readonly PrefixCodeDecoder ScaleFactorCodebook = new(AacHuffmanData.ScaleFactorCodes, AacHuffmanData.ScaleFactorLengths);

    /// <summary>Spectral codebooks 1 to 11 (index 0 unused).</summary>
    public static readonly PrefixCodeDecoder?[] SpectralCodebooks =
    [
        null,
        new(AacHuffmanData.Book1Codes, AacHuffmanData.Book1Lengths),
        new(AacHuffmanData.Book2Codes, AacHuffmanData.Book2Lengths),
        new(AacHuffmanData.Book3Codes, AacHuffmanData.Book3Lengths),
        new(AacHuffmanData.Book4Codes, AacHuffmanData.Book4Lengths),
        new(AacHuffmanData.Book5Codes, AacHuffmanData.Book5Lengths),
        new(AacHuffmanData.Book6Codes, AacHuffmanData.Book6Lengths),
        new(AacHuffmanData.Book7Codes, AacHuffmanData.Book7Lengths),
        new(AacHuffmanData.Book8Codes, AacHuffmanData.Book8Lengths),
        new(AacHuffmanData.Book9Codes, AacHuffmanData.Book9Lengths),
        new(AacHuffmanData.Book10Codes, AacHuffmanData.Book10Lengths),
        new(AacHuffmanData.Book11Codes, AacHuffmanData.Book11Lengths),
    ];

    /// <summary>Long-window band offsets, 88.2/96 kHz.</summary>
    private static readonly int[] Long96 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96, 108, 120, 132, 144, 156, 172, 188, 212,
        240, 276, 320, 384, 448, 512, 576, 640, 704, 768, 832, 896, 960, 1024,
    ];

    /// <summary>Long-window band offsets, 64 kHz.</summary>
    private static readonly int[] Long64 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 100, 112, 124, 140, 156, 172, 192, 216, 240,
        268, 304, 344, 384, 424, 464, 504, 544, 584, 624, 664, 704, 744, 784, 824, 864, 904, 944, 984, 1024,
    ];

    /// <summary>Long-window band offsets, 44.1/48 kHz.</summary>
    private static readonly int[] Long48 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80, 88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264,
        292, 320, 352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896, 928, 1024,
    ];

    /// <summary>Long-window band offsets, 32 kHz.</summary>
    private static readonly int[] Long32 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80, 88, 96, 108, 120, 132, 144, 160, 176, 196, 216, 240, 264,
        292, 320, 352, 384, 416, 448, 480, 512, 544, 576, 608, 640, 672, 704, 736, 768, 800, 832, 864, 896, 928, 960, 992, 1024,
    ];

    /// <summary>Long-window band offsets, 22.05/24 kHz.</summary>
    private static readonly int[] Long24 =
    [
        0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 52, 60, 68, 76, 84, 92, 100, 108, 116, 124, 136, 148, 160, 172, 188, 204,
        220, 240, 260, 284, 308, 336, 364, 396, 432, 468, 508, 552, 600, 652, 704, 768, 832, 896, 960, 1024,
    ];

    /// <summary>Long-window band offsets, 11.025/12/16 kHz.</summary>
    private static readonly int[] Long16 =
    [
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 100, 112, 124, 136, 148, 160, 172, 184, 196, 212, 228, 244, 260, 280,
        300, 320, 344, 368, 396, 424, 456, 492, 532, 572, 616, 664, 716, 772, 832, 896, 960, 1024,
    ];

    /// <summary>Long-window band offsets, 7.35/8 kHz.</summary>
    private static readonly int[] Long8 =
    [
        0, 12, 24, 36, 48, 60, 72, 84, 96, 108, 120, 132, 144, 156, 172, 188, 204, 220, 236, 252, 268, 288, 308, 328, 348,
        372, 396, 420, 448, 476, 508, 544, 580, 620, 664, 712, 764, 820, 880, 944, 1024,
    ];

    /// <summary>Short-window band offsets, 64/88.2/96 kHz.</summary>
    private static readonly int[] Short96 = [0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 92, 128];

    /// <summary>Short-window band offsets, 32/44.1/48 kHz.</summary>
    private static readonly int[] Short48 = [0, 4, 8, 12, 16, 20, 28, 36, 44, 56, 68, 80, 96, 112, 128];

    /// <summary>Short-window band offsets, 22.05/24 kHz.</summary>
    private static readonly int[] Short24 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 64, 76, 92, 108, 128];

    /// <summary>Short-window band offsets, 11.025/12/16 kHz.</summary>
    private static readonly int[] Short16 = [0, 4, 8, 12, 16, 20, 24, 28, 32, 40, 48, 60, 72, 88, 108, 128];

    /// <summary>Short-window band offsets, 7.35/8 kHz.</summary>
    private static readonly int[] Short8 = [0, 4, 8, 12, 16, 20, 24, 28, 36, 44, 52, 60, 72, 88, 108, 128];

    /// <summary>Long-window band offsets by sampling frequency index; band count is length - 1.</summary>
    public static readonly int[][] LongBandOffsets = [Long96, Long96, Long64, Long48, Long48, Long32, Long24, Long24, Long16, Long16, Long16, Long8, Long8];

    /// <summary>Short-window band offsets by sampling frequency index; band count is length - 1.</summary>
    public static readonly int[][] ShortBandOffsets = [Short96, Short96, Short96, Short48, Short48, Short48, Short24, Short24, Short16, Short16, Short16, Short8, Short8];

    /// <summary>Builds the inverse quantisation table.</summary>
    /// <returns>The table.</returns>
    private static float[] CreateInverseQuantisation()
    {
        var table = new float[MaxQuantisedMagnitude + 1];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(i, 4.0 / 3.0);
        }

        return table;
    }

    /// <summary>Builds the scalefactor gain table.</summary>
    /// <returns>The table.</returns>
    private static float[] CreateScaleFactorGain()
    {
        var table = new float[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (float)Math.Pow(2, 0.25 * (i - 100));
        }

        return table;
    }
}
