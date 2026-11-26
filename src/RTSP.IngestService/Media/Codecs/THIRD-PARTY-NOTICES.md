# Managed codecs — source provenance and third-party notices

Every component of `Media/Codecs` is managed C# in this project. No native library, P/Invoke, external process or
codec package is used. This file records where each component's algorithm or data comes from. It is updated whenever
a component is ported or adapted.

## Provenance record

| Component | Files | Basis | Third-party material | Licence | Modifications |
| --- | --- | --- | --- | --- | --- |
| Bitstream reader/writer, Exp-Golomb | `Common/Bitstreams/*` | ITU-T H.264 §9.1 (Exp-Golomb definition) | None | Ours | — |
| Prefix-code decoder | `Common/Entropy/PrefixCodeDecoder.cs` | Generic two-level table lookup | None | Ours | — |
| FFT, DCT-IV, forward and inverse MDCT | `Common/Transforms/ComplexFft.cs`, `DctIv.cs`, `ForwardMdct.cs`, `InverseMdct.cs` | Radix-2 FFT; IMDCT definition from ISO/IEC 14496-3 §4.6.11.3.1; the forward MDCT as its transpose; both computed through a DCT-IV | None | Ours | — |
| Window functions | `Common/Transforms/WindowFunctions.cs` | Sine and KBD windows from ISO/IEC 14496-3 §4.6.11.3.2; Kaiser window and I0 power series (textbook definitions) | None | Ours | — |
| Polyphase resampler | `Audio/Processing/ResamplerFilter.cs`, `AudioResampler.cs` | Windowed-sinc polyphase interpolation (textbook rational sample-rate conversion); Kaiser's design formulas for beta and length | None. Written from the published formulas, not from any resampler implementation | Ours | — |
| Stereo downmix, sample format, canonical converter | `Audio/Processing/StereoDownmixer.cs`, `AudioSampleFormat.cs`, `CanonicalAudioConverter.cs` | ITU-R BS.775 downmix coefficients (-3 dB centre and surround, LFE omitted) | None | Ours | — |
| AAC-LC encoder | `Audio/Aac/Encoder/*` | ISO/IEC 14496-3 subpart 4 bitstream syntax (§4.4) and the inverse of its decoding process; quantiser and rounding offset 0.4054 as commonly described for AAC; block switching per §4.6.11.3; spectral-flatness tonality (Johnston, 1988); Terhardt's absolute-threshold formula (1979) | None. Written from the specification and published formulas, not translated from any encoder. It uses the Huffman and band tables listed above | Ours | — |
| ADTS framing | `Audio/Aac/AdtsFraming.cs` | ISO/IEC 14496-3 §1.A.2 `adts_fixed_header` / `adts_variable_header` | None | Ours | — |
| Managed audio transcoder and smoke command | `Audio/ManagedAudioTranscoder.cs`, `Hosting/CodecSmokeCommand.cs` | Composition of the components above | None | Ours | — |
| AAC-LC bitstream syntax and decoding process | `Audio/Aac/Decoder/AacDecoder.cs`, `AacChannelStream.cs`, `AacFilterbank.cs` | ISO/IEC 14496-3:2019 subpart 4: syntax (§4.4), decoding of ICS, scalefactors, spectral data, pulses (§4.6.1 to §4.6.3), M/S and intensity (§4.6.8), TNS (§4.6.9), PNS (§4.6.13), filterbank and windows (§4.6.11) | None. Written from the specification, not translated from any implementation | Ours | — |
| AAC Huffman codebooks | `Audio/Aac/AacHuffmanData.cs` | ISO/IEC 14496-3 Tables 4.A.1 to 4.A.12 | Data extracted mechanically from `huff_ltab*` / `huff_ctab*` in `aacenc/src/aac_rom.c`, VisualOn vo-aacenc, https://github.com/mstorsjo/vo-aacenc (master branch, retrieved 2026-11-26) | Apache License 2.0, Copyright 2003-2010 VisualOn, Inc. | Packed two-codebook length tables split per codebook; re-expressed as C# spans. Validated by tests: prefix-free, complete (Kraft sum 1), expected symbol counts |
| AAC scalefactor band offsets | `Audio/Aac/AacTables.cs` | ISO/IEC 14496-3 Tables 4.129 to 4.147 | Values transcribed from `sfBandTabLong` / `sfBandTabShort` in the same `aac_rom.c` | Apache License 2.0, Copyright 2003-2010 VisualOn, Inc. | Re-expressed as one offset array per table; 7350 Hz mapped to the 8 kHz tables |
| AAC TNS maximum bands | `Audio/Aac/AacTables.cs` | ISO/IEC 14496-3 Table 4.156 (AAC LC rows) | None | Ours | — |
| AAC window shapes and sequences | `Audio/Aac/AacWindows.cs` | ISO/IEC 14496-3 §4.6.11.3.2, computed at start-up; shared by the decoder's synthesis and the encoder's analysis | None | Ours | — |
| H.264 NAL units, parameter sets, slice headers | `Video/H264/H264NalUnits.cs`, `H264ParameterSets.cs`, `H264SliceHeader.cs` | ITU-T H.264 (08/2021) §7.3, §7.4 (syntax and semantics), Annex B (byte stream), Annex E (VUI); ISO/IEC 14496-15 §5.3.3 (`AVCDecoderConfigurationRecord`) | None | Ours | — |
| H.264 CAVLC tables and parsing | `Video/H264/H264Tables.cs`, `H264CavlcSyntax.cs` | ITU-T H.264 §9.2, Tables 9-5 to 9-10; zig-zag scans (§8.5.6), default scaling lists (Tables 7-3, 7-4), chroma QP (Table 8-15), deblocking alpha/beta/tC0 (Tables 8-16, 8-17), LevelScale normalisation (8-315, 8-318), MaxDpbMbs (Table A-1) | None. Transcribed from the specification | Ours | — |
| H.264 CABAC tables | `Video/H264/H264CabacTables.cs` | ITU-T H.264 §9.3: context initialisation (m, n) for ctxIdx 0 to 459 (Tables 9-12 to 9-24), rangeTabLPS (Table 9-44), state transitions (Table 9-45) | Data extracted mechanically (script recorded in the Phase 3 report) from `g_kiCabacGlobalContextIdx`, `g_kuiCabacRangeLps` and `g_kuiStateTransTable` in `codec/common/src/common_tables.cpp`, Cisco OpenH264 v2.6.0, https://github.com/cisco/openh264, tag `v2.6.0` = commit `652bdb7719f30b52b08e506645a7322ff1b2cc6f`, file SHA-256 `2CF0A5FC3FCE813413566AF2CED506EB91A1E0C3F625ABC9CA9BF70E0FFEF958` | BSD 2-Clause, Copyright (c) 2013, Cisco Systems | Re-expressed as C# spans, one (m, n) pair list per model; `CTX_NA` entries as 0. Validated by tests (table sizes, spot values) and by bit-exact decoding of four CABAC encoders' streams |
| H.264 CABAC engine and syntax | `Video/H264/H264CabacReader.cs`, `H264CabacSyntax.cs`, `H264SliceDecoder.Contexts.cs` | ITU-T H.264 §9.3.1 (initialisation), §9.3.2 (binarisation), §9.3.3 (context index derivation, arithmetic decoding engine) | None. Written from the specification | Ours | — |
| H.264 reconstruction | `Video/H264/H264SliceDecoder*.cs`, `H264IntraPrediction.cs`, `H264InterPrediction.cs`, `H264Transform.cs` | ITU-T H.264 §7.3.5 (macroblock layer), §8.3 (intra prediction), §8.4 (inter prediction, motion vector prediction, direct modes, weighted prediction, interpolation), §8.5 (scaling and transforms), §8.7 (deblocking) | None. Written from the specification | Ours | — |
| H.264 decoding process and picture management | `Video/H264/H264Decoder*.cs`, `H264Picture.cs`, `H264DecoderOptions.cs`, `Media/Codecs/VideoFrame.cs` | ITU-T H.264 §8.2.1 (picture order count), §8.2.4 (reference lists), §8.2.5 (reference marking), §8.2.5.2 (frame_num gaps), Annex C.4 (DPB output, bumping) | None | Ours | — |

Codec patents are a separate matter from copyright. See `.artifacts/codecs/embedded-codec-licensing-assessment.md`:
an AAC decoder or encoder written in managed code is subject to the same AAC patent considerations as any other
implementation, and the same holds for the H.264 decoder and AVC patent licensing.

## Apache License 2.0 notice (vo-aacenc)

```
Copyright 2003-2010, VisualOn, Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

Attribution notice from the vo-aacenc `NOTICE` file (Apache-2.0 §4(d)); the Unicode licence that file also contains
does not pertain to the material used here:

```
Additional Codecs
These files are Copyright 2003-2010 VisualOn, but released under
the Apache2 License.
```

Apache-2.0 §4(a) requires recipients of the binary to receive a copy of the licence, and §4(d) requires the
attribution above in a NOTICE file, the documentation or a notices display. Release packaging must therefore include
the licence text and this attribution. That packaging change has not been made yet; see the managed codec
implementation report.

## BSD 2-Clause notice (Cisco OpenH264)

```
Copyright (c) 2013, Cisco Systems
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

The BSD 2-Clause licence requires binary redistributions to reproduce this notice in the documentation or other
materials. As with the Apache-2.0 material above, release packaging must include it; that packaging change has not
been made yet.
