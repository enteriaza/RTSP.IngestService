// Constant tables of ITU-T H.264, transcribed from the specification. Scans and matrices are in raster order
// (index = y * width + x). The CAVLC tables are validated at type initialisation by PrefixCodeDecoder (prefix-free)
// and by tests (permutations, completeness), and the decoder as a whole is validated bit-exactly against an
// independent decoder.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>H.264 constant tables.</summary>
internal static class H264Tables
{
    /// <summary>Gets the 4x4 frame zig-zag scan (Table 8-12): scan index to raster position.</summary>
    public static ReadOnlySpan<byte> Zigzag4x4 => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>Gets the 8x8 frame zig-zag scan (Table 8-13): scan index to raster position.</summary>
    public static ReadOnlySpan<byte> Zigzag8x8 =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Gets coded_block_pattern for Intra_4x4 and Intra_8x8 macroblocks by me(v) codeNum (Table 9-4, ChromaArrayType 1).</summary>
    public static ReadOnlySpan<byte> CodedBlockPatternIntra =>
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46,
        16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    /// <summary>Gets coded_block_pattern for Inter macroblocks by me(v) codeNum (Table 9-4, ChromaArrayType 1).</summary>
    public static ReadOnlySpan<byte> CodedBlockPatternInter =>
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13,
        14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];

    /// <summary>Gets QPc by qPI (Table 8-15).</summary>
    public static ReadOnlySpan<byte> ChromaQp =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 29, 30,
        31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38,
        39, 39, 39, 39,
    ];

    /// <summary>Gets the 4x4 normalisation values v (8-315) at <c>m * 3 + column</c>.</summary>
    public static ReadOnlySpan<byte> NormAdjust4x4 =>
    [
        10, 16, 13,
        11, 18, 14,
        13, 20, 16,
        14, 23, 18,
        16, 25, 20,
        18, 29, 23,
    ];

    /// <summary>Gets the 8x8 normalisation values v (8-318) at <c>m * 6 + column</c>.</summary>
    public static ReadOnlySpan<byte> NormAdjust8x8 =>
    [
        20, 18, 32, 19, 25, 24,
        22, 19, 35, 21, 28, 26,
        26, 23, 42, 24, 33, 31,
        28, 25, 45, 26, 35, 33,
        32, 28, 51, 30, 40, 38,
        36, 32, 58, 34, 46, 43,
    ];

    /// <summary>Gets Default_4x4_Intra (Table 7-3) in raster order.</summary>
    public static ReadOnlySpan<byte> Default4x4Intra => [6, 13, 20, 28, 13, 20, 28, 32, 20, 28, 32, 37, 28, 32, 37, 42];

    /// <summary>Gets Default_4x4_Inter (Table 7-3) in raster order.</summary>
    public static ReadOnlySpan<byte> Default4x4Inter => [10, 14, 20, 24, 14, 20, 24, 27, 20, 24, 27, 30, 24, 27, 30, 34];

    /// <summary>Gets Default_8x8_Intra (Table 7-4) in raster order.</summary>
    public static ReadOnlySpan<byte> Default8x8Intra =>
    [
        6, 10, 13, 16, 18, 23, 25, 27,
        10, 11, 16, 18, 23, 25, 27, 29,
        13, 16, 18, 23, 25, 27, 29, 31,
        16, 18, 23, 25, 27, 29, 31, 33,
        18, 23, 25, 27, 29, 31, 33, 36,
        23, 25, 27, 29, 31, 33, 36, 38,
        25, 27, 29, 31, 33, 36, 38, 40,
        27, 29, 31, 33, 36, 38, 40, 42,
    ];

    /// <summary>Gets Default_8x8_Inter (Table 7-4) in raster order.</summary>
    public static ReadOnlySpan<byte> Default8x8Inter =>
    [
        9, 13, 15, 17, 19, 21, 22, 24,
        13, 13, 17, 19, 21, 22, 24, 25,
        15, 17, 19, 21, 22, 24, 25, 27,
        17, 19, 21, 22, 24, 25, 27, 28,
        19, 21, 22, 24, 25, 27, 28, 30,
        21, 22, 24, 25, 27, 28, 30, 32,
        22, 24, 25, 27, 28, 30, 32, 33,
        24, 25, 27, 28, 30, 32, 33, 35,
    ];

    /// <summary>Gets the deblocking alpha' by indexA (Table 8-16).</summary>
    public static ReadOnlySpan<byte> Alpha =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 5, 6, 7, 8, 9, 10, 12, 13, 15, 17, 20, 22, 25, 28,
        32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182,
        203, 226, 255, 255,
    ];

    /// <summary>Gets the deblocking beta' by indexB (Table 8-16).</summary>
    public static ReadOnlySpan<byte> Beta =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16,
        17, 17, 18, 18,
    ];

    /// <summary>Gets the deblocking tC0' at <c>indexA * 3 + bS - 1</c> (Table 8-17).</summary>
    public static ReadOnlySpan<byte> Tc0 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 2, 3,
        1, 2, 3, 2, 2, 3, 2, 2, 4, 2, 3, 4, 2, 3, 4, 3, 3, 5, 3, 4, 6, 3, 4, 6,
        4, 5, 7, 4, 5, 8, 4, 6, 9, 5, 7, 10, 6, 8, 11, 6, 8, 13, 7, 10, 14, 8, 11, 16,
        9, 12, 18, 10, 13, 20, 11, 15, 23, 13, 17, 25,
    ];

    /// <summary>Gets the CABAC significant_coeff_flag ctxIdxInc for 8x8 frame blocks by levelListIdx (Table 9-43).</summary>
    public static ReadOnlySpan<byte> SignificantInc8x8 =>
    [
        0, 1, 2, 3, 4, 5, 5, 4, 4, 3, 3, 4, 4, 4, 5, 5,
        4, 4, 4, 4, 3, 3, 6, 7, 7, 7, 8, 9, 10, 9, 8, 7,
        7, 6, 11, 12, 13, 11, 6, 7, 8, 9, 14, 10, 9, 8, 6, 11,
        12, 13, 11, 6, 9, 14, 10, 9, 11, 12, 13, 11, 14, 10, 12, 0,
    ];

    /// <summary>Gets the CABAC last_significant_coeff_flag ctxIdxInc for 8x8 frame blocks by levelListIdx (Table 9-43).</summary>
    public static ReadOnlySpan<byte> LastInc8x8 =>
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8,
    ];

    /// <summary>
    /// Gets coeff_token codeword lengths at <c>table * 68 + TotalCoeff * 4 + TrailingOnes</c> (Table 9-5); table 0 is
    /// 0 &lt;= nC &lt; 2, 1 is 2 &lt;= nC &lt; 4, 2 is 4 &lt;= nC &lt; 8. Zero marks an impossible combination.
    /// </summary>
    public static ReadOnlySpan<byte> CoeffTokenLengths =>
    [
        1, 0, 0, 0, 6, 2, 0, 0, 8, 6, 3, 0, 9, 8, 7, 5, 10, 9, 8, 6,
        11, 10, 9, 7, 13, 11, 10, 8, 13, 13, 11, 9, 13, 13, 13, 10,
        14, 14, 13, 11, 14, 14, 14, 13, 15, 15, 14, 14, 15, 15, 15, 14,
        16, 15, 15, 15, 16, 16, 16, 15, 16, 16, 16, 16, 16, 16, 16, 16,

        2, 0, 0, 0, 6, 2, 0, 0, 6, 5, 3, 0, 7, 6, 6, 4, 8, 6, 6, 4,
        8, 7, 7, 5, 9, 8, 8, 6, 11, 9, 9, 6, 11, 11, 11, 7,
        12, 11, 11, 9, 12, 12, 12, 11, 12, 12, 12, 11, 13, 13, 13, 12,
        13, 13, 13, 13, 13, 14, 13, 13, 14, 14, 14, 13, 14, 14, 14, 14,

        4, 0, 0, 0, 6, 4, 0, 0, 6, 5, 4, 0, 6, 5, 5, 4, 7, 5, 5, 4,
        7, 5, 5, 4, 7, 6, 6, 4, 7, 6, 6, 4, 8, 7, 7, 5,
        8, 8, 7, 6, 9, 8, 8, 7, 9, 9, 8, 8, 9, 9, 9, 8,
        10, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
    ];

    /// <summary>Gets coeff_token codewords, laid out as <see cref="CoeffTokenLengths"/>.</summary>
    public static ReadOnlySpan<byte> CoeffTokenCodes =>
    [
        1, 0, 0, 0, 5, 1, 0, 0, 7, 4, 1, 0, 7, 6, 5, 3, 7, 6, 5, 3,
        7, 6, 5, 4, 15, 6, 5, 4, 11, 14, 5, 4, 8, 10, 13, 4,
        15, 14, 9, 4, 11, 10, 13, 12, 15, 14, 9, 12, 11, 10, 13, 8,
        15, 1, 9, 12, 11, 14, 13, 8, 7, 10, 9, 12, 4, 6, 5, 8,

        3, 0, 0, 0, 11, 2, 0, 0, 7, 7, 3, 0, 7, 10, 9, 5, 7, 6, 5, 4,
        4, 6, 5, 6, 7, 6, 5, 8, 15, 6, 5, 4, 11, 14, 13, 4,
        15, 10, 9, 4, 11, 14, 13, 12, 8, 10, 9, 8, 15, 14, 13, 12,
        11, 10, 9, 12, 7, 11, 6, 8, 9, 8, 10, 1, 7, 6, 5, 4,

        15, 0, 0, 0, 15, 14, 0, 0, 11, 15, 13, 0, 8, 12, 14, 12, 15, 10, 11, 11,
        11, 8, 9, 10, 9, 14, 13, 9, 8, 10, 9, 8, 15, 14, 13, 13,
        11, 14, 10, 12, 15, 10, 13, 12, 11, 14, 9, 12, 8, 10, 13, 8,
        13, 7, 9, 12, 9, 12, 11, 10, 5, 8, 7, 6, 1, 4, 3, 2,
    ];

    /// <summary>Gets chroma DC (nC = -1) coeff_token lengths at <c>TotalCoeff * 4 + TrailingOnes</c> (Table 9-5).</summary>
    public static ReadOnlySpan<byte> ChromaDcCoeffTokenLengths => [2, 0, 0, 0, 6, 1, 0, 0, 6, 6, 3, 0, 6, 7, 7, 6, 6, 8, 8, 7];

    /// <summary>Gets chroma DC coeff_token codewords.</summary>
    public static ReadOnlySpan<byte> ChromaDcCoeffTokenCodes => [1, 0, 0, 0, 7, 1, 0, 0, 4, 6, 1, 0, 3, 3, 2, 5, 2, 3, 2, 0];

    /// <summary>Gets total_zeros lengths for 4x4 blocks at <c>(TotalCoeff - 1) * 16 + total_zeros</c> (Tables 9-7, 9-8).</summary>
    public static ReadOnlySpan<byte> TotalZerosLengths =>
    [
        1, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 9,
        3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6, 0,
        4, 3, 3, 3, 4, 4, 3, 3, 4, 5, 5, 6, 5, 6, 0, 0,
        5, 3, 4, 4, 3, 3, 3, 4, 3, 4, 5, 5, 5, 0, 0, 0,
        4, 4, 4, 3, 3, 3, 3, 3, 4, 5, 4, 5, 0, 0, 0, 0,
        6, 5, 3, 3, 3, 3, 3, 3, 4, 3, 6, 0, 0, 0, 0, 0,
        6, 5, 3, 3, 3, 2, 3, 4, 3, 6, 0, 0, 0, 0, 0, 0,
        6, 4, 5, 3, 2, 2, 3, 3, 6, 0, 0, 0, 0, 0, 0, 0,
        6, 6, 4, 2, 2, 3, 2, 5, 0, 0, 0, 0, 0, 0, 0, 0,
        5, 5, 3, 2, 2, 2, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 3, 3, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 2, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 3, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>Gets total_zeros codewords for 4x4 blocks, laid out as <see cref="TotalZerosLengths"/>.</summary>
    public static ReadOnlySpan<byte> TotalZerosCodes =>
    [
        1, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 1,
        7, 6, 5, 4, 3, 5, 4, 3, 2, 3, 2, 3, 2, 1, 0, 0,
        5, 7, 6, 5, 4, 3, 4, 3, 2, 3, 2, 1, 1, 0, 0, 0,
        3, 7, 5, 4, 6, 5, 4, 3, 3, 2, 2, 1, 0, 0, 0, 0,
        5, 4, 3, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0, 0, 0, 0,
        1, 1, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0,
        1, 1, 5, 4, 3, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 3, 3, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 1, 3, 2, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 1, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 2, 1, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>Gets total_zeros lengths for 2x2 chroma DC at <c>(TotalCoeff - 1) * 4 + total_zeros</c> (Table 9-9a).</summary>
    public static ReadOnlySpan<byte> ChromaDcTotalZerosLengths => [1, 2, 3, 3, 1, 2, 2, 0, 1, 1, 0, 0];

    /// <summary>Gets total_zeros codewords for 2x2 chroma DC.</summary>
    public static ReadOnlySpan<byte> ChromaDcTotalZerosCodes => [1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0];

    /// <summary>Gets run_before lengths at <c>(Min(zerosLeft, 7) - 1) * 15 + run_before</c> (Table 9-10).</summary>
    public static ReadOnlySpan<byte> RunBeforeLengths =>
    [
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 3, 3, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11,
    ];

    /// <summary>Gets run_before codewords, laid out as <see cref="RunBeforeLengths"/>.</summary>
    public static ReadOnlySpan<byte> RunBeforeCodes =>
    [
        1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 0, 1, 3, 2, 5, 4, 0, 0, 0, 0, 0, 0, 0, 0,
        7, 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>Returns MaxDpbMbs for a level (Table A-1).</summary>
    /// <param name="levelIdc">level_idc.</param>
    /// <param name="constraintSet3">constraint_set3_flag, which with level_idc 11 marks level 1b in Baseline and Main.</param>
    /// <param name="profileIdc">profile_idc.</param>
    /// <returns>MaxDpbMbs, or 0 for an unknown level.</returns>
    public static int MaxDpbMbs(int levelIdc, bool constraintSet3, int profileIdc)
    {
        if (levelIdc == 11 && constraintSet3 && profileIdc is 66 or 77)
        {
            return 396;
        }

        return levelIdc switch
        {
            9 or 10 => 396,
            11 => 900,
            12 or 13 or 20 => 2376,
            21 => 4752,
            22 or 30 => 8100,
            31 => 18000,
            32 => 20480,
            40 or 41 => 32768,
            42 => 34816,
            50 => 110400,
            51 or 52 => 184320,
            60 or 61 or 62 => 696320,
            _ => 0,
        };
    }
}
