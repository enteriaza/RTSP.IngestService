// CABAC syntax elements (ITU-T H.264 9.3.2 binarizations, 9.3.3.1 context selection). Context indices are the
// ctxIdxOffset values of Table 9-34 plus ctxIdxInc; neighbour-dependent increments come from the slice decoder.

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reads CABAC macroblock syntax.</summary>
/// <param name="engine">The arithmetic decoding engine.</param>
internal ref struct H264CabacSyntax(H264CabacReader engine) : IH264EntropyReader
{
    /// <summary>The arithmetic decoding engine.</summary>
    public H264CabacReader Engine = engine;

    /// <inheritdoc/>
    public readonly bool IsCabac => true;

    /// <summary>Gets ctxBlockCatOffset of coded_block_flag by category (Table 9-40).</summary>
    private static ReadOnlySpan<byte> CodedBlockFlagOffset => [0, 4, 8, 12, 16];

    /// <summary>Gets ctxBlockCatOffset of significant_coeff_flag and last_significant_coeff_flag by category.</summary>
    private static ReadOnlySpan<byte> SignificantOffset => [0, 15, 29, 44, 47];

    /// <summary>Gets ctxBlockCatOffset of coeff_abs_level_minus1 by category.</summary>
    private static ReadOnlySpan<byte> LevelOffset => [0, 10, 20, 30, 39];

    /// <summary>Reads mb_skip_flag.</summary>
    /// <param name="d">The slice decoder.</param>
    /// <returns>The flag.</returns>
    public bool SkipFlag(H264SliceDecoder d) =>
        Engine.Decision((d.SliceType == H264SliceType.P ? 11 : 24) + d.SkipInc()) != 0;

    /// <inheritdoc/>
    public int MbType(H264SliceDecoder d)
    {
        switch (d.SliceType)
        {
            case H264SliceType.I:
                return IntraMbType(3 + d.IntraTypeInc(), 3 + 2, true);
            case H264SliceType.P:
                if (Engine.Decision(14) == 0)
                {
                    return Engine.Decision(15) == 0 ? 3 * Engine.Decision(16) : 2 - Engine.Decision(17);
                }

                return 5 + IntraMbType(17, 17, false);
            default:
                {
                    if (Engine.Decision(27 + d.BTypeInc()) == 0)
                    {
                        return 0;
                    }

                    if (Engine.Decision(27 + 3) == 0)
                    {
                        return 1 + Engine.Decision(27 + 5);
                    }

                    var bits = Engine.Decision(27 + 4) << 3;
                    bits |= Engine.Decision(27 + 5) << 2;
                    bits |= Engine.Decision(27 + 5) << 1;
                    bits |= Engine.Decision(27 + 5);
                    return bits switch
                    {
                        < 8 => bits + 3,
                        13 => 23 + IntraMbType(32, 32, false),
                        14 => 11,
                        15 => 22,
                        _ => ((bits << 1) | Engine.Decision(27 + 5)) - 4,
                    };
                }
        }
    }

    /// <inheritdoc/>
    public bool Transform8x8(H264SliceDecoder d) => Engine.Decision(399 + d.Transform8x8Inc()) != 0;

    /// <inheritdoc/>
    public int IntraPredMode()
    {
        if (Engine.Decision(68) != 0)
        {
            return -1;
        }

        var mode = Engine.Decision(69);
        mode |= Engine.Decision(69) << 1;
        mode |= Engine.Decision(69) << 2;
        return mode;
    }

    /// <inheritdoc/>
    public int IntraChromaPredMode(H264SliceDecoder d)
    {
        if (Engine.Decision(64 + d.ChromaPredInc()) == 0)
        {
            return 0;
        }

        if (Engine.Decision(64 + 3) == 0)
        {
            return 1;
        }

        return Engine.Decision(64 + 3) == 0 ? 2 : 3;
    }

    /// <inheritdoc/>
    public int SubMbType(H264SliceDecoder d)
    {
        if (d.SliceType == H264SliceType.P)
        {
            if (Engine.Decision(21) != 0)
            {
                return 0;
            }

            if (Engine.Decision(22) == 0)
            {
                return 1;
            }

            return Engine.Decision(23) != 0 ? 2 : 3;
        }

        if (Engine.Decision(36) == 0)
        {
            return 0;
        }

        if (Engine.Decision(37) == 0)
        {
            return 1 + Engine.Decision(39);
        }

        var type = 3;
        if (Engine.Decision(38) != 0)
        {
            if (Engine.Decision(39) != 0)
            {
                return 11 + Engine.Decision(39);
            }

            type += 4;
        }

        type += 2 * Engine.Decision(39);
        type += Engine.Decision(39);
        return type;
    }

    /// <inheritdoc/>
    public int RefIdx(H264SliceDecoder d, int list, int bx, int by)
    {
        if (Engine.Decision(54 + d.RefIdxInc(list, bx, by)) == 0)
        {
            return 0;
        }

        var value = 1;
        var ctx = 54 + 4;
        while (Engine.Decision(ctx) != 0)
        {
            ctx = 54 + 5;
            if (++value > 31)
            {
                CodecException.InvalidData("ref_idx is out of range.");
            }
        }

        return value;
    }

    /// <inheritdoc/>
    public int Mvd(H264SliceDecoder d, int list, int component, int bx, int by)
    {
        var offset = component == 0 ? 40 : 47;
        var sum = d.MvdSum(list, component, bx, by);
        if (Engine.Decision(offset + (sum < 3 ? 0 : sum > 32 ? 2 : 1)) == 0)
        {
            return 0;
        }

        var value = 1;
        while (value < 9 && Engine.Decision(offset + Math.Min(value + 2, 6)) != 0)
        {
            value++;
        }

        if (value >= 9)
        {
            value += ExpGolombBypass(3);
        }

        if (value > 1 << 15)
        {
            CodecException.InvalidData("mvd is out of range.");
        }

        return Engine.Bypass() != 0 ? -value : value;
    }

    /// <inheritdoc/>
    public int CodedBlockPattern(H264SliceDecoder d)
    {
        var luma = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            luma |= Engine.Decision(73 + d.CbpLumaInc(b8, luma)) << b8;
        }

        var chroma = 0;
        if (Engine.Decision(77 + d.CbpChromaInc(0)) != 0)
        {
            chroma = 1 + Engine.Decision(77 + d.CbpChromaInc(1));
        }

        return luma | (chroma << 4);
    }

    /// <inheritdoc/>
    public int MbQpDelta(H264SliceDecoder d)
    {
        if (Engine.Decision(60 + (d.PrevQpDeltaNonZero ? 1 : 0)) == 0)
        {
            return 0;
        }

        var k = 1;
        var ctx = 60 + 2;
        while (Engine.Decision(ctx) != 0)
        {
            ctx = 60 + 3;
            if (++k > 52)
            {
                CodecException.InvalidData("mb_qp_delta is out of range.");
            }
        }

        return (k & 1) != 0 ? (k + 1) >> 1 : -(k >> 1);
    }

    /// <inheritdoc/>
    public int ResidualBlock(H264SliceDecoder d, in H264ResidualBlock block, int startIdx, int endIdx, int maxNumCoeff, Span<int> levels)
    {
        var cat = (int)block.Category;
        if (block.Category != H264BlockCategory.Luma8x8
            && Engine.Decision(85 + CodedBlockFlagOffset[cat] + d.CodedBlockFlagInc(block)) == 0)
        {
            return 0;
        }

        Span<bool> significant = stackalloc bool[64];
        var numCoeff = endIdx + 1;
        var i = startIdx;
        var eight = block.Category == H264BlockCategory.Luma8x8;
        for (; i < numCoeff - 1; i++)
        {
            int sigCtx, lastCtx;
            if (eight)
            {
                sigCtx = 402 + H264Tables.SignificantInc8x8[i];
                lastCtx = 417 + H264Tables.LastInc8x8[i];
            }
            else
            {
                var inc = block.Category == H264BlockCategory.ChromaDc ? Math.Min(i, 2) : i;
                sigCtx = 105 + SignificantOffset[cat] + inc;
                lastCtx = 166 + SignificantOffset[cat] + inc;
            }

            if (Engine.Decision(sigCtx) == 0)
            {
                continue;
            }

            significant[i] = true;
            if (Engine.Decision(lastCtx) != 0)
            {
                numCoeff = i + 1;
                break;
            }
        }

        significant[numCoeff - 1] = true;
        var levelBase = eight ? 426 : 227 + LevelOffset[cat];
        var gt1Cap = block.Category == H264BlockCategory.ChromaDc ? 3 : 4;
        int numGt1 = 0, numEq1 = 0, count = 0;
        for (var k = numCoeff - 1; k >= startIdx; k--)
        {
            if (!significant[k])
            {
                continue;
            }

            var abs = 1;
            if (Engine.Decision(levelBase + (numGt1 != 0 ? 0 : Math.Min(4, 1 + numEq1))) != 0)
            {
                var ctx = levelBase + 5 + Math.Min(gt1Cap, numGt1);
                abs = 2;
                while (abs < 15 && Engine.Decision(ctx) != 0)
                {
                    abs++;
                }

                if (abs >= 15)
                {
                    abs += ExpGolombBypass(0);
                }
            }

            if (abs > H264SliceDecoder.MaxCoefficient)
            {
                CodecException.InvalidData("A coefficient level is out of range.");
            }

            levels[k] = Engine.Bypass() != 0 ? -abs : abs;
            if (abs == 1)
            {
                numEq1++;
            }
            else
            {
                numGt1++;
            }

            count++;
        }

        return count;
    }

    /// <inheritdoc/>
    public void Pcm(Span<byte> samples) => Engine.ReadPcm(samples);

    /// <summary>Reads a k-th order Exp-Golomb suffix in bypass mode (9.3.2.3).</summary>
    /// <param name="k">The order.</param>
    /// <returns>The value.</returns>
    private int ExpGolombBypass(int k)
    {
        var value = 0;
        while (Engine.Bypass() != 0)
        {
            value += 1 << k;
            if (++k > 20)
            {
                CodecException.InvalidData("An Exp-Golomb suffix is too long.");
            }
        }

        while (k-- > 0)
        {
            value += Engine.Bypass() << k;
        }

        return value;
    }

    /// <summary>Decodes an intra mb_type in an I slice or as the suffix in P and B slices.</summary>
    /// <param name="firstCtx">The context of the first bin.</param>
    /// <param name="baseCtx">The base for the remaining bins (I: ctxIdxOffset + 2; P/B: the suffix offset).</param>
    /// <param name="intraSlice">Whether this is an I slice.</param>
    /// <returns>The I mb_type (0 I_NxN, 1 to 24 Intra16x16, 25 I_PCM).</returns>
    private int IntraMbType(int firstCtx, int baseCtx, bool intraSlice)
    {
        if (Engine.Decision(firstCtx) == 0)
        {
            return 0;
        }

        if (Engine.Terminate() != 0)
        {
            return 25;
        }

        var s = intraSlice ? 1 : 0;
        var type = 1 + (12 * Engine.Decision(baseCtx + 1));
        if (Engine.Decision(baseCtx + 2) != 0)
        {
            type += 4 + (4 * Engine.Decision(baseCtx + 2 + s));
        }

        type += 2 * Engine.Decision(baseCtx + 3 + s);
        type += Engine.Decision(baseCtx + 3 + (2 * s));
        return type;
    }
}
