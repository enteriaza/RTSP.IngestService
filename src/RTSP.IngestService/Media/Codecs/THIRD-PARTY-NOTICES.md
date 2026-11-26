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

Codec patents are a separate matter from copyright. See `.artifacts/codecs/embedded-codec-licensing-assessment.md`:
an AAC decoder or encoder written in managed code is subject to the same AAC patent considerations as any other
implementation.

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
