using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Codecs;
using NAudio.FileFormats.Mp3;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace uAudio.Services
{
    /// <summary>
    /// Universal audio decoder that handles format detection by file content/header and extension,
    /// WaveFormatExtensible PCM/IEEE float unwrapping, A-Law/Mu-Law decoding, non-standard WAV chunks,
    /// OGG Vorbis, Media Foundation (with FileStream fallback for special/Unicode/long paths),
    /// and multi-stage MP3/ACM/DMO fallbacks.
    /// </summary>
    public sealed class AudioSourceReader : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly Stream? _ownedBaseStream;
        private readonly WaveStream _waveStream;
        private readonly ISampleProvider _sampleProvider;
        private readonly double _durationSeconds;
        private bool _disposed;

        private AudioSourceReader(
            string filePath,
            WaveStream waveStream,
            ISampleProvider sampleProvider,
            Stream? ownedBaseStream,
            double durationSeconds)
        {
            FilePath = filePath;
            _waveStream = waveStream;
            _sampleProvider = sampleProvider;
            _ownedBaseStream = ownedBaseStream;
            _durationSeconds = durationSeconds;
            WaveFormat = sampleProvider.WaveFormat;
        }

        public string FilePath { get; }

        public WaveFormat WaveFormat { get; }

        public double TotalSeconds => _durationSeconds;

        public TimeSpan TotalTime => TimeSpan.FromSeconds(_durationSeconds);

        public TimeSpan CurrentTime
        {
            get
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    return _waveStream.CurrentTime;
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                    double clampedSeconds = Math.Clamp(value.TotalSeconds, 0d, Math.Max(0d, _durationSeconds));
                    long bytePosition = (long)Math.Round(clampedSeconds * _waveStream.WaveFormat.AverageBytesPerSecond);
                    int blockAlign = Math.Max(1, _waveStream.BlockAlign);
                    bytePosition -= bytePosition % blockAlign;
                    bytePosition = Math.Clamp(bytePosition, 0L, Math.Max(0L, _waveStream.Length));
                    _waveStream.Position = bytePosition;
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (count == 0)
                {
                    return 0;
                }

                return _sampleProvider.Read(buffer, offset, count);
            }
        }

        public static AudioSourceReader Open(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Audio file path is empty.", nameof(filePath));
            }

            string fullPath = Path.GetFullPath(filePath.Trim().Trim('"'));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Audio file was not found.", fullPath);
            }

            DetectedContainer container = DetectContainer(fullPath);
            string extension = Path.GetExtension(fullPath);

            Exception? primaryException = null;

            // 1. Container-matched primary readers
            if (container == DetectedContainer.Wave)
            {
                if (TryOpenWave(fullPath, out AudioSourceReader? waveReader, out Exception? waveEx) && waveReader != null)
                {
                    return waveReader;
                }

                primaryException = waveEx;
            }
            else if (container == DetectedContainer.Aiff)
            {
                if (TryOpenAiff(fullPath, out AudioSourceReader? aiffReader, out Exception? aiffEx) && aiffReader != null)
                {
                    return aiffReader;
                }

                primaryException = aiffEx;
            }
            else if (container == DetectedContainer.Ogg)
            {
                if (TryOpenVorbis(fullPath, out AudioSourceReader? vorbisReader, out Exception? vorbisEx) && vorbisReader != null)
                {
                    return vorbisReader;
                }

                primaryException = vorbisEx;
            }
            else if (container == DetectedContainer.Mp3 || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                if (TryOpenMp3(fullPath, out AudioSourceReader? mp3Reader, out Exception? mp3Ex) && mp3Reader != null)
                {
                    return mp3Reader;
                }

                primaryException = mp3Ex;
            }

            // 2. Extension-based attempts if header differed from extension (e.g. custom wrapper or ID3 prefix)
            if (container != DetectedContainer.Ogg &&
                (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(extension, ".oga", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryOpenVorbis(fullPath, out AudioSourceReader? vorbisReader, out Exception? vorbisEx) && vorbisReader != null)
                {
                    return vorbisReader;
                }

                primaryException ??= vorbisEx;
            }

            if (container != DetectedContainer.Wave &&
                string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                if (TryOpenWave(fullPath, out AudioSourceReader? waveReader, out Exception? waveEx) && waveReader != null)
                {
                    return waveReader;
                }

                primaryException ??= waveEx;
            }

            if (container != DetectedContainer.Aiff &&
                (string.Equals(extension, ".aiff", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(extension, ".aif", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryOpenAiff(fullPath, out AudioSourceReader? aiffReader, out Exception? aiffEx) && aiffReader != null)
                {
                    return aiffReader;
                }

                primaryException ??= aiffEx;
            }

            // 3. Windows Media Foundation (URL and ByteStream fallbacks — handles MP3, AAC, M4A, FLAC, WMA, WAV, MP4, etc.)
            if (OperatingSystem.IsWindows())
            {
                if (TryOpenMediaFoundation(fullPath, out AudioSourceReader? mfReader, out Exception? mfEx) && mfReader != null)
                {
                    return mfReader;
                }

                primaryException ??= mfEx;
            }

            // 4. Managed MP3 fallback in case a file with .wav or other extension actually contains MPEG frames
            if (container != DetectedContainer.Mp3 && !string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                if (TryOpenMp3(fullPath, out AudioSourceReader? mp3Fallback, out _) && mp3Fallback != null)
                {
                    return mp3Fallback;
                }
            }

            throw primaryException ?? new NotSupportedException($"Audio format could not be decoded: {Path.GetFileName(fullPath)}");
        }

        private static bool TryOpenWave(string fullPath, out AudioSourceReader? result, out Exception? error)
        {
            result = null;
            error = null;
            FileStream? stream = null;
            WaveStream? waveStream = null;

            try
            {
                stream = OpenSharedReadStream(fullPath);

                try
                {
                    waveStream = new WaveFileReader(stream);
                }
                catch
                {
                    stream.Position = 0;
                    waveStream = RelaxedRiffWaveStream.Open(stream);
                }

                AudioSourceReader? built = BuildFromWaveStream(fullPath, waveStream, stream);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                waveStream.Dispose();
                stream.Dispose();
            }
            catch (Exception ex)
            {
                error = ex;
                waveStream?.Dispose();
                stream?.Dispose();
            }

            // Fallback to MediaFoundation for exotic compressed WAV codecs (e.g. IMA ADPCM, GSM, MPEG-in-WAV)
            if (OperatingSystem.IsWindows() && TryOpenMediaFoundation(fullPath, out result, out Exception? mfError))
            {
                return true;
            }

            error ??= mfError;
            return false;
        }

        private static bool TryOpenAiff(string fullPath, out AudioSourceReader? result, out Exception? error)
        {
            result = null;
            error = null;
            FileStream? stream = null;
            WaveStream? waveStream = null;

            try
            {
                stream = OpenSharedReadStream(fullPath);
                waveStream = new AiffFileReader(stream);

                AudioSourceReader? built = BuildFromWaveStream(fullPath, waveStream, stream);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                waveStream.Dispose();
                stream.Dispose();
            }
            catch (Exception ex)
            {
                error = ex;
                waveStream?.Dispose();
                stream?.Dispose();
            }

            return false;
        }

        private static bool TryOpenVorbis(string fullPath, out AudioSourceReader? result, out Exception? error)
        {
            result = null;
            error = null;
            FileStream? stream = null;
            VorbisWaveReader? vorbis = null;

            try
            {
                stream = OpenSharedReadStream(fullPath);
                vorbis = new VorbisWaveReader(stream, closeOnDispose: false);
                double duration = vorbis.TotalTime.TotalSeconds;

                if (duration > 0d &&
                    !double.IsNaN(duration) &&
                    !double.IsInfinity(duration) &&
                    vorbis.WaveFormat.SampleRate > 0 &&
                    vorbis.WaveFormat.Channels > 0)
                {
                    result = new AudioSourceReader(fullPath, vorbis, vorbis, stream, duration);
                    return true;
                }

                vorbis.Dispose();
                stream.Dispose();
                error = new InvalidDataException("OGG Vorbis file has no readable audio duration.");
            }
            catch (Exception ex)
            {
                error = ex;
                vorbis?.Dispose();
                stream?.Dispose();
            }

            return false;
        }

        private static bool TryOpenMp3(string fullPath, out AudioSourceReader? result, out Exception? error)
        {
            result = null;
            error = null;

            // 1. Media Foundation first on Windows (handles VBR, AAC-in-MP3-extension, corrupted frames cleanly)
            if (OperatingSystem.IsWindows())
            {
                if (TryOpenMediaFoundation(fullPath, out result, out Exception? mfError) && result != null)
                {
                    return true;
                }

                error = mfError;
            }

            // 2. Fully managed NLayer MP3 decoder (works on Windows N/KN/Server without Media Feature Pack and on non-Windows)
            FileStream? nlayerStream = null;
            WaveStream? nlayerReader = null;
            try
            {
                nlayerStream = OpenSharedReadStream(fullPath);
                nlayerReader = new Mp3FileReaderBase(
                    nlayerStream,
                    wf => new NLayer.NAudioSupport.Mp3FrameDecompressor(wf));

                AudioSourceReader? built = BuildFromWaveStream(fullPath, nlayerReader, nlayerStream);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                nlayerReader.Dispose();
                nlayerStream.Dispose();
            }
            catch (Exception ex)
            {
                error ??= ex;
                nlayerReader?.Dispose();
                nlayerStream?.Dispose();
            }

            // 3. Direct NLayer ManagedMpegStream fallback (handles raw/unusual ID3 headers that Mp3FileReaderBase rejects)
            FileStream? rawMpegStream = null;
            WaveStream? managedMpeg = null;
            try
            {
                rawMpegStream = OpenSharedReadStream(fullPath);
                managedMpeg = new NLayer.NAudioSupport.ManagedMpegStream(rawMpegStream, closeOnDispose: false);

                AudioSourceReader? built = BuildFromWaveStream(fullPath, managedMpeg, rawMpegStream);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                managedMpeg.Dispose();
                rawMpegStream.Dispose();
            }
            catch (Exception ex)
            {
                error ??= ex;
                managedMpeg?.Dispose();
                rawMpegStream?.Dispose();
            }

            // 4. Windows DMO / ACM MP3 decoders as final fallback
            if (OperatingSystem.IsWindows())
            {
                FileStream? dmoStream = null;
                WaveStream? dmoReader = null;
                try
                {
                    dmoStream = OpenSharedReadStream(fullPath);
                    dmoReader = new Mp3FileReaderBase(dmoStream, wf => new DmoMp3FrameDecompressor(wf));
                    AudioSourceReader? built = BuildFromWaveStream(fullPath, dmoReader, dmoStream);
                    if (built != null)
                    {
                        result = built;
                        return true;
                    }

                    dmoReader.Dispose();
                    dmoStream.Dispose();
                }
                catch (Exception ex)
                {
                    error ??= ex;
                    dmoReader?.Dispose();
                    dmoStream?.Dispose();
                }

                FileStream? acmStream = null;
                WaveStream? acmReader = null;
                try
                {
                    acmStream = OpenSharedReadStream(fullPath);
                    acmReader = new Mp3FileReader(acmStream);
                    AudioSourceReader? built = BuildFromWaveStream(fullPath, acmReader, acmStream);
                    if (built != null)
                    {
                        result = built;
                        return true;
                    }

                    acmReader.Dispose();
                    acmStream.Dispose();
                }
                catch (Exception ex)
                {
                    error ??= ex;
                    acmReader?.Dispose();
                    acmStream?.Dispose();
                }
            }

            return false;
        }

        private static bool TryOpenMediaFoundation(string fullPath, out AudioSourceReader? result, out Exception? error)
        {
            result = null;
            error = null;

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            // Attempt 1: StreamMediaFoundationReader over FileStream.
            // Avoids MFCreateSourceReaderFromURL bugs with #, %, ?, &, Unicode/Cyrillic, UNC, and >260 char paths!
            FileStream? byteStreamSource = null;
            WaveStream? streamMfReader = null;
            try
            {
                byteStreamSource = OpenSharedReadStream(fullPath);
                streamMfReader = new StreamMediaFoundationReader(
                    byteStreamSource,
                    new MediaFoundationReader.MediaFoundationReaderSettings
                    {
                        RepositionInRead = true
                    });

                AudioSourceReader? built = BuildFromWaveStream(fullPath, streamMfReader, byteStreamSource);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                streamMfReader.Dispose();
                byteStreamSource.Dispose();
            }
            catch (Exception ex)
            {
                error = ex;
                streamMfReader?.Dispose();
                byteStreamSource?.Dispose();
            }

            // Attempt 2: Classic MediaFoundationReader(filePath) (sometimes uses file extension hints in MF)
            WaveStream? urlMfReader = null;
            try
            {
                urlMfReader = new MediaFoundationReader(
                    fullPath,
                    new MediaFoundationReader.MediaFoundationReaderSettings
                    {
                        RepositionInRead = true
                    });

                AudioSourceReader? built = BuildFromWaveStream(fullPath, urlMfReader, ownedBaseStream: null);
                if (built != null)
                {
                    result = built;
                    return true;
                }

                urlMfReader.Dispose();
            }
            catch (Exception ex)
            {
                error ??= ex;
                urlMfReader?.Dispose();
            }

            return false;
        }

        private static AudioSourceReader? BuildFromWaveStream(string fullPath, WaveStream rawStream, Stream? ownedBaseStream)
        {
            WaveStream workingStream = NormalizeWaveStream(rawStream);
            ISampleProvider sampleProvider = ConvertToSampleProvider(workingStream);

            if (sampleProvider.WaveFormat.SampleRate <= 0 || sampleProvider.WaveFormat.Channels <= 0)
            {
                if (!ReferenceEquals(workingStream, rawStream))
                {
                    workingStream.Dispose();
                }

                return null;
            }

            double duration = workingStream.TotalTime.TotalSeconds;
            if (duration <= 0d || double.IsNaN(duration) || double.IsInfinity(duration))
            {
                duration = EstimateDurationByReading(workingStream);
                workingStream.Position = 0;
            }

            if (duration <= 0d || double.IsNaN(duration) || double.IsInfinity(duration))
            {
                if (!ReferenceEquals(workingStream, rawStream))
                {
                    workingStream.Dispose();
                }

                return null;
            }

            return new AudioSourceReader(fullPath, workingStream, sampleProvider, ownedBaseStream, duration);
        }

        private static WaveStream NormalizeWaveStream(WaveStream stream)
        {
            WaveFormat format = UnwrapWaveFormat(stream.WaveFormat);

            if (format.Encoding == WaveFormatEncoding.Pcm || format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                if (!ReferenceEquals(format, stream.WaveFormat))
                {
                    return new FormatOverridingWaveStream(stream, format);
                }

                return stream;
            }

            if (format.Encoding == WaveFormatEncoding.ALaw || format.Encoding == WaveFormatEncoding.MuLaw)
            {
                return new G711DecodingWaveStream(stream, format.Encoding == WaveFormatEncoding.ALaw);
            }

            if (OperatingSystem.IsWindows())
            {
                WaveStream pcmStream = WaveFormatConversionStream.CreatePcmStream(stream);
                return new BlockAlignReductionStream(pcmStream);
            }

            throw new NotSupportedException($"Unsupported compressed audio encoding: {format.Encoding}");
        }

        private static WaveFormat UnwrapWaveFormat(WaveFormat format)
        {
            if (format is WaveFormatExtensible extensible)
            {
                WaveFormat standard = extensible.ToStandardWaveFormat();
                if (standard.Encoding == WaveFormatEncoding.Pcm || standard.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    return standard;
                }

                // Some tools write WaveFormatExtensible with 64-bit float or non-32-bit float where ToStandardWaveFormat keeps Extensible
                byte[] subFormatBytes = extensible.SubFormat.ToByteArray();
                int subFormatTag = BitConverter.ToUInt16(subFormatBytes, 0);
                if (subFormatTag == (int)WaveFormatEncoding.Pcm)
                {
                    return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
                }

                if (subFormatTag == (int)WaveFormatEncoding.IeeeFloat)
                {
                    return format.BitsPerSample == 32
                        ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
                        : WaveFormat.CreateCustomFormat(
                            WaveFormatEncoding.IeeeFloat,
                            format.SampleRate,
                            format.Channels,
                            format.SampleRate * format.Channels * (format.BitsPerSample / 8),
                            format.Channels * (format.BitsPerSample / 8),
                            format.BitsPerSample);
                }

                if (subFormatTag == (int)WaveFormatEncoding.ALaw || subFormatTag == (int)WaveFormatEncoding.MuLaw)
                {
                    return WaveFormat.CreateCustomFormat(
                        (WaveFormatEncoding)subFormatTag,
                        format.SampleRate,
                        format.Channels,
                        format.SampleRate * format.Channels,
                        format.Channels,
                        8);
                }
            }

            return format;
        }

        private static ISampleProvider ConvertToSampleProvider(IWaveProvider waveProvider)
        {
            WaveFormat wf = waveProvider.WaveFormat;

            if (wf.Encoding == WaveFormatEncoding.Pcm)
            {
                return wf.BitsPerSample switch
                {
                    8 => new Pcm8BitToSampleProvider(waveProvider),
                    16 => new Pcm16BitToSampleProvider(waveProvider),
                    24 => new Pcm24BitToSampleProvider(waveProvider),
                    32 => new Pcm32BitToSampleProvider(waveProvider),
                    _ => throw new NotSupportedException($"Unsupported PCM bit depth: {wf.BitsPerSample}")
                };
            }

            if (wf.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return wf.BitsPerSample switch
                {
                    32 => new SafeFloatWaveToSampleProvider(waveProvider),
                    64 => new WaveToSampleProvider64(waveProvider),
                    _ => throw new NotSupportedException($"Unsupported IEEE float bit depth: {wf.BitsPerSample}")
                };
            }

            throw new NotSupportedException($"Unsupported wave encoding: {wf.Encoding}");
        }

        private static double EstimateDurationByReading(WaveStream stream)
        {
            if (stream.WaveFormat.AverageBytesPerSecond <= 0)
            {
                return 0d;
            }

            int blockAlign = Math.Max(1, stream.BlockAlign);
            int bufferSize = Math.Max(blockAlign * 4096, stream.WaveFormat.AverageBytesPerSecond);
            bufferSize -= bufferSize % blockAlign;
            byte[] buffer = new byte[Math.Max(blockAlign, bufferSize)];

            long totalBytes = 0;
            const long maxProbeBytes = 512L * 1024L * 1024L;

            while (totalBytes < maxProbeBytes)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                totalBytes += read;
            }

            return (double)totalBytes / stream.WaveFormat.AverageBytesPerSecond;
        }

        private static FileStream OpenSharedReadStream(string fullPath)
        {
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private enum DetectedContainer
        {
            Unknown,
            Wave,
            Aiff,
            Ogg,
            Flac,
            Mp3,
            Mp4OrM4a,
            AsfWma
        }

        private static DetectedContainer DetectContainer(string fullPath)
        {
            try
            {
                using FileStream fs = OpenSharedReadStream(fullPath);
                Span<byte> header = stackalloc byte[36];
                int read = fs.Read(header);
                if (read < 4)
                {
                    return DetectedContainer.Unknown;
                }

                if (read >= 12 &&
                    (header.Slice(0, 4).SequenceEqual("RIFF"u8) || header.Slice(0, 4).SequenceEqual("RF64"u8)) &&
                    header.Slice(8, 4).SequenceEqual("WAVE"u8))
                {
                    return DetectedContainer.Wave;
                }

                if (read >= 12 &&
                    header.Slice(0, 4).SequenceEqual("FORM"u8) &&
                    (header.Slice(8, 4).SequenceEqual("AIFF"u8) || header.Slice(8, 4).SequenceEqual("AIFC"u8)))
                {
                    return DetectedContainer.Aiff;
                }

                if (header.Slice(0, 4).SequenceEqual("OggS"u8))
                {
                    return DetectedContainer.Ogg;
                }

                if (header.Slice(0, 4).SequenceEqual("fLaC"u8))
                {
                    return DetectedContainer.Flac;
                }

                if (header.Slice(0, 3).SequenceEqual("ID3"u8))
                {
                    return DetectedContainer.Mp3;
                }

                if (read >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x18) != 0x08 && (header[1] & 0x06) != 0x00)
                {
                    return DetectedContainer.Mp3;
                }

                if (read >= 8 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
                {
                    return DetectedContainer.Mp4OrM4a;
                }

                if (read >= 16)
                {
                    ReadOnlySpan<byte> asfGuid = new byte[]
                    {
                        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
                        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
                    };

                    if (header.Slice(0, 16).SequenceEqual(asfGuid))
                    {
                        return DetectedContainer.AsfWma;
                    }
                }
            }
            catch
            {
                // Let caller handle I/O exceptions properly during stream open.
            }

            return DetectedContainer.Unknown;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioSourceReader));
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _waveStream.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"Error disposing wave stream for {FilePath}", ex);
                }

                try
                {
                    _ownedBaseStream?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning($"Error disposing file stream for {FilePath}", ex);
                }
            }
        }

        /// <summary>
        /// Wraps a WaveStream whose underlying bytes are already valid PCM or IEEE float,
        /// overriding a WaveFormatExtensible descriptor with its standard WaveFormat so NAudio sample converters accept it.
        /// </summary>
        private sealed class FormatOverridingWaveStream : WaveStream
        {
            private readonly WaveStream _inner;
            private readonly WaveFormat _format;

            public FormatOverridingWaveStream(WaveStream inner, WaveFormat format)
            {
                _inner = inner;
                _format = format;
            }

            public override WaveFormat WaveFormat => _format;

            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override int BlockAlign => _format.BlockAlign > 0 ? _format.BlockAlign : _inner.BlockAlign;

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _inner.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Managed G.711 A-Law / Mu-Law to 16-bit PCM stream decoder (avoids needing ACM codecs).
        /// </summary>
        private sealed class G711DecodingWaveStream : WaveStream
        {
            private readonly WaveStream _source;
            private readonly bool _isALaw;
            private readonly WaveFormat _pcmFormat;
            private byte[] _sourceBuffer = Array.Empty<byte>();

            public G711DecodingWaveStream(WaveStream source, bool isALaw)
            {
                _source = source;
                _isALaw = isALaw;
                _pcmFormat = new WaveFormat(source.WaveFormat.SampleRate, 16, source.WaveFormat.Channels);
            }

            public override WaveFormat WaveFormat => _pcmFormat;

            public override long Length => _source.Length * 2L;

            public override long Position
            {
                get => _source.Position * 2L;
                set
                {
                    long sourcePos = Math.Clamp(value / 2L, 0L, _source.Length);
                    int sourceAlign = Math.Max(1, _source.WaveFormat.Channels);
                    sourcePos -= sourcePos % sourceAlign;
                    _source.Position = sourcePos;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int alignedBytes = count - (count % _pcmFormat.BlockAlign);
                if (alignedBytes <= 0)
                {
                    return 0;
                }

                int sourceBytesToRead = alignedBytes / 2;
                if (_sourceBuffer.Length < sourceBytesToRead)
                {
                    _sourceBuffer = new byte[sourceBytesToRead];
                }

                int sourceRead = _source.Read(_sourceBuffer, 0, sourceBytesToRead);
                if (sourceRead <= 0)
                {
                    return 0;
                }

                int outIndex = offset;
                for (int i = 0; i < sourceRead; i++)
                {
                    short sample = _isALaw
                        ? ALawDecoder.ALawToLinearSample(_sourceBuffer[i])
                        : MuLawDecoder.MuLawToLinearSample(_sourceBuffer[i]);

                    buffer[outIndex++] = (byte)(sample & 0xFF);
                    buffer[outIndex++] = (byte)((sample >> 8) & 0xFF);
                }

                return sourceRead * 2;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _source.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Safe IEEE 32-bit float converter that sanitizes NaN/Inf samples and avoids unsafe pointer alignment pitfalls.
        /// </summary>
        private sealed class SafeFloatWaveToSampleProvider : ISampleProvider
        {
            private readonly IWaveProvider _source;
            private readonly WaveFormat _waveFormat;
            private byte[] _byteBuffer = Array.Empty<byte>();

            public SafeFloatWaveToSampleProvider(IWaveProvider source)
            {
                _source = source;
                _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
            }

            public WaveFormat WaveFormat => _waveFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int bytesNeeded = count * 4;
                if (_byteBuffer.Length < bytesNeeded)
                {
                    _byteBuffer = new byte[bytesNeeded];
                }

                int bytesRead = _source.Read(_byteBuffer, 0, bytesNeeded);
                int samplesRead = bytesRead / 4;
                if (samplesRead <= 0)
                {
                    return 0;
                }

                ReadOnlySpan<float> floatSpan = MemoryMarshal.Cast<byte, float>(_byteBuffer.AsSpan(0, samplesRead * 4));
                for (int i = 0; i < samplesRead; i++)
                {
                    float sample = floatSpan[i];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        sample = 0f;
                    }

                    buffer[offset + i] = sample;
                }

                return samplesRead;
            }
        }

        /// <summary>
        /// Fallback RIFF/WAVE reader for files produced by ffmpeg, recorders, or browsers where
        /// riffSize or dataChunkSize is 0xFFFFFFFF (streaming WAV) or has non-zero padding bytes
        /// that cause NAudio's WaveFileChunkReader to fail.
        /// </summary>
        private sealed class RelaxedRiffWaveStream : WaveStream
        {
            private readonly Stream _stream;
            private readonly WaveFormat _waveFormat;
            private readonly long _dataPosition;
            private readonly long _dataLength;
            private long _position;

            private RelaxedRiffWaveStream(Stream stream, WaveFormat waveFormat, long dataPosition, long dataLength)
            {
                _stream = stream;
                _waveFormat = waveFormat;
                _dataPosition = dataPosition;
                _dataLength = dataLength;
                _stream.Position = _dataPosition;
            }

            public static RelaxedRiffWaveStream Open(Stream stream)
            {
                using BinaryReader br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                if (stream.Length < 12)
                {
                    throw new FormatException("WAV file is too short.");
                }

                string riffId = new string(br.ReadChars(4));
                if (riffId != "RIFF" && riffId != "RF64")
                {
                    throw new FormatException("Missing RIFF header.");
                }

                _ = br.ReadUInt32(); // ignore potentially bogus/0xFFFFFFFF RIFF size
                string waveId = new string(br.ReadChars(4));
                if (waveId != "WAVE")
                {
                    throw new FormatException("Missing WAVE signature.");
                }

                WaveFormat? waveFormat = null;
                long dataPos = -1;
                long dataLen = 0;

                while (stream.Position <= stream.Length - 8)
                {
                    byte[] chunkIdBytes = br.ReadBytes(4);
                    if (chunkIdBytes.Length < 4)
                    {
                        break;
                    }

                    string chunkId = Encoding.ASCII.GetString(chunkIdBytes);
                    uint chunkSize = br.ReadUInt32();
                    long chunkDataStart = stream.Position;

                    if (chunkId == "fmt ")
                    {
                        int fmtSize = (int)Math.Min(chunkSize, Math.Max(0L, stream.Length - chunkDataStart));
                        if (fmtSize < 16)
                        {
                            throw new FormatException("Invalid WAV fmt chunk.");
                        }

                        waveFormat = WaveFormat.FromFormatChunk(br, fmtSize);
                        stream.Position = chunkDataStart + fmtSize;
                    }
                    else if (chunkId == "data")
                    {
                        dataPos = chunkDataStart;
                        long remainingInFile = Math.Max(0L, stream.Length - chunkDataStart);
                        dataLen = chunkSize == uint.MaxValue || chunkSize == 0 || chunkSize > remainingInFile
                            ? remainingInFile
                            : chunkSize;
                        break;
                    }
                    else
                    {
                        long skip = Math.Min((long)chunkSize, Math.Max(0L, stream.Length - chunkDataStart));
                        stream.Position = chunkDataStart + skip;
                    }

                    if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
                    {
                        stream.Position++;
                    }
                }

                if (waveFormat == null || dataPos < 0)
                {
                    throw new FormatException("WAV file is missing fmt or data chunk.");
                }

                int blockAlign = Math.Max(1, waveFormat.BlockAlign);
                dataLen -= dataLen % blockAlign;

                return new RelaxedRiffWaveStream(stream, waveFormat, dataPos, dataLen);
            }

            public override WaveFormat WaveFormat => _waveFormat;

            public override long Length => _dataLength;

            public override long Position
            {
                get => _position;
                set
                {
                    int align = Math.Max(1, BlockAlign);
                    long clamped = Math.Clamp(value, 0L, _dataLength);
                    clamped -= clamped % align;
                    _position = clamped;
                    _stream.Position = _dataPosition + _position;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int align = Math.Max(1, BlockAlign);
                int toRead = count - (count % align);
                long remaining = _dataLength - _position;
                if (remaining <= 0 || toRead <= 0)
                {
                    return 0;
                }

                if (toRead > remaining)
                {
                    toRead = (int)(remaining - (remaining % align));
                }

                if (toRead <= 0)
                {
                    return 0;
                }

                if (_stream.Position != _dataPosition + _position)
                {
                    _stream.Position = _dataPosition + _position;
                }

                int read = _stream.Read(buffer, offset, toRead);
                _position += read;
                return read;
            }
        }
    }
}
