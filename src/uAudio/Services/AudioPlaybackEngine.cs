using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using uAudio;
using uAudio.Models;

namespace uAudio.Services
{
    public sealed class TrackSampleProvider : ISampleProvider, IDisposable
    {
        private const double SeekToleranceSamples = 2d;

        private readonly object _stateSync = new object();
        private readonly object _contextSync = new object();
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly WaveFormat _waveFormat;
        private readonly Dictionary<string, FilePlaybackContext> _fileContexts;

        private List<AudioClipModel> _clips;
        private double _currentGlobalTime;
        private float _volume;
        private bool _isMuted;
        private bool _disposed;
        private long _stateVersion;

        private Guid _continuationClipId;
        private string? _continuationFilePath;
        private double _expectedFileTime;
        private bool _hasContinuation;

        private double TimeEpsilonSeconds => Math.Max(AppConstants.TimeComparisonEpsilonSeconds, 1d / Math.Max(_sampleRate, 1));

        private sealed class FilePlaybackContext : IDisposable
        {
            private readonly object _syncRoot = new object();
            private readonly int _targetSampleRate;
            private readonly int _targetChannels;
            private bool _disposed;

            public AudioFileReader Reader { get; }
            public ISampleProvider Provider { get; private set; }

            public FilePlaybackContext(string filePath, int targetSampleRate, int targetChannels)
            {
                _targetSampleRate = targetSampleRate;
                _targetChannels = targetChannels;
                Reader = new AudioFileReader(filePath);
                Provider = BuildProvider();
            }

            public int ReadFrom(double seconds, bool seekBeforeRead, float[] buffer, int offset, int count)
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return 0;
                    }

                    if (seekBeforeRead)
                    {
                        SeekCore(seconds);
                    }

                    return Provider.Read(buffer, offset, count);
                }
            }

            private void SeekCore(double seconds)
            {
                double clampedSeconds = Math.Max(0d, Math.Min(seconds, Reader.TotalTime.TotalSeconds));
                Reader.CurrentTime = TimeSpan.FromSeconds(clampedSeconds);
                Provider = BuildProvider();
            }

            private ISampleProvider BuildProvider()
            {
                ISampleProvider provider = Reader;

                if (provider.WaveFormat.Channels != _targetChannels)
                {
                    provider = CreateChannelMappedProvider(provider, _targetChannels);
                }

                if (provider.WaveFormat.SampleRate != _targetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, _targetSampleRate);
                }

                return provider;
            }

            public void Dispose()
            {
                lock (_syncRoot)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Reader.Dispose();
                    _disposed = true;
                }
            }
        }

        public TrackSampleProvider(int sampleRate = AppConstants.DefaultSampleRate, int channels = AppConstants.DefaultChannelCount)
        {
            _sampleRate = Math.Max(1, sampleRate);
            _channels = Math.Max(1, channels);
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            _fileContexts = new Dictionary<string, FilePlaybackContext>(StringComparer.OrdinalIgnoreCase);
            _clips = new List<AudioClipModel>();
            _volume = AppConstants.DefaultVolume;
            _continuationClipId = Guid.Empty;
        }

        public WaveFormat WaveFormat => _waveFormat;

        public double CurrentGlobalTime
        {
            get
            {
                lock (_stateSync)
                {
                    return _currentGlobalTime;
                }
            }
        }

        public float Volume
        {
            get
            {
                lock (_stateSync)
                {
                    return _volume;
                }
            }
            set
            {
                lock (_stateSync)
                {
                    _volume = Math.Clamp(value, AppConstants.MinVolume, AppConstants.MaxVolume);
                }
            }
        }

        public bool IsMuted
        {
            get
            {
                lock (_stateSync)
                {
                    return _isMuted;
                }
            }
            set
            {
                lock (_stateSync)
                {
                    _isMuted = value;
                }
            }
        }

        public void UpdateClips(List<AudioClipModel> clips)
        {
            List<AudioClipModel> clonedClips = (clips ?? new List<AudioClipModel>())
                .Select(c => c.Clone())
                .OrderBy(c => c.StartTime)
                .ThenBy(c => c.Id)
                .ToList();

            lock (_stateSync)
            {
                ThrowIfDisposed();

                _clips = clonedClips;
                ResetContinuationLocked();
                _stateVersion++;
            }

            CleanupUnusedContexts(clonedClips);
        }

        public void Seek(double globalTime)
        {
            lock (_stateSync)
            {
                ThrowIfDisposed();

                _currentGlobalTime = Math.Max(0d, globalTime);
                ResetContinuationLocked();
                _stateVersion++;
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

            Array.Clear(buffer, offset, count);

            List<AudioClipModel> clips;
            double currentTime;
            float volume;
            bool isMuted;
            Guid continuationClipId;
            string? continuationFilePath;
            double expectedFileTime;
            bool hasContinuation;
            long version;

            lock (_stateSync)
            {
                ThrowIfDisposed();

                clips = _clips;
                currentTime = _currentGlobalTime;
                volume = _volume;
                isMuted = _isMuted;
                continuationClipId = _continuationClipId;
                continuationFilePath = _continuationFilePath;
                expectedFileTime = _expectedFileTime;
                hasContinuation = _hasContinuation;
                version = _stateVersion;
            }

            int samplesWritten = 0;
            double epsilon = TimeEpsilonSeconds;

            while (samplesWritten < count)
            {
                AudioClipModel? activeClip = FindClipAtTime(clips, currentTime, epsilon);

                if (activeClip == null)
                {
                    int silenceSamples = CalculateSilenceSamples(clips, count - samplesWritten, currentTime, epsilon);
                    samplesWritten += silenceSamples;
                    currentTime += SamplesToSeconds(silenceSamples);
                    hasContinuation = false;
                    continue;
                }

                double localTime = Math.Max(0d, currentTime - activeClip.StartTime);
                double remainingClipTime = activeClip.Duration - localTime;

                if (remainingClipTime <= epsilon)
                {
                    currentTime = activeClip.EndTime;
                    hasContinuation = false;
                    continue;
                }

                int clipSamplesRemaining = SecondsToSamples(remainingClipTime);
                if (clipSamplesRemaining <= 0)
                {
                    clipSamplesRemaining = Math.Min(count - samplesWritten, _channels);
                }

                int blockSamples = Math.Min(count - samplesWritten, clipSamplesRemaining);
                double blockDuration = SamplesToSeconds(blockSamples);

                if (isMuted || volume <= AppConstants.MinVolume)
                {
                    samplesWritten += blockSamples;
                    currentTime += blockDuration;
                    hasContinuation = false;
                    continue;
                }

                FilePlaybackContext? context = GetOrCreateContext(activeClip.FilePath);
                if (context == null)
                {
                    samplesWritten += blockSamples;
                    currentTime += blockDuration;
                    hasContinuation = false;
                    continue;
                }

                double targetFileTime = Math.Max(0d, activeClip.OffsetInFile + localTime);
                bool seekBeforeRead = ShouldSeekContext(activeClip, targetFileTime, hasContinuation, continuationClipId, continuationFilePath, expectedFileTime);
                int actualRead = context.ReadFrom(targetFileTime, seekBeforeRead, buffer, offset + samplesWritten, blockSamples);

                if (actualRead < blockSamples)
                {
                    Array.Clear(buffer, offset + samplesWritten + Math.Max(actualRead, 0), blockSamples - Math.Max(actualRead, 0));
                }

                if (actualRead > 0 && Math.Abs(volume - AppConstants.DefaultVolume) > 0.0001f)
                {
                    for (int i = 0; i < actualRead; i++)
                    {
                        buffer[offset + samplesWritten + i] *= volume;
                    }
                }

                samplesWritten += blockSamples;
                currentTime += blockDuration;

                if (actualRead == blockSamples)
                {
                    continuationClipId = activeClip.Id;
                    continuationFilePath = activeClip.FilePath;
                    expectedFileTime = targetFileTime + blockDuration;
                    hasContinuation = true;
                }
                else
                {
                    hasContinuation = false;
                }
            }

            lock (_stateSync)
            {
                if (!_disposed && _stateVersion == version)
                {
                    _currentGlobalTime = currentTime;
                    _continuationClipId = continuationClipId;
                    _continuationFilePath = continuationFilePath;
                    _expectedFileTime = expectedFileTime;
                    _hasContinuation = hasContinuation;
                }
            }

            return count;
        }

        private bool ShouldSeekContext(
            AudioClipModel clip,
            double targetFileTime,
            bool hasContinuation,
            Guid continuationClipId,
            string? continuationFilePath,
            double expectedFileTime)
        {
            double toleranceSeconds = SeekToleranceSamples / _sampleRate;

            return !hasContinuation ||
                continuationClipId != clip.Id ||
                !string.Equals(continuationFilePath, clip.FilePath, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(expectedFileTime - targetFileTime) > toleranceSeconds;
        }

        private static AudioClipModel? FindClipAtTime(List<AudioClipModel> clips, double globalTime, double epsilon)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                AudioClipModel clip = clips[i];
                if (globalTime + epsilon >= clip.StartTime && globalTime < clip.EndTime - epsilon)
                {
                    return clip;
                }
            }

            return null;
        }

        private static AudioClipModel? FindNextClipAfter(List<AudioClipModel> clips, double globalTime, double epsilon)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                AudioClipModel clip = clips[i];
                if (clip.StartTime > globalTime + epsilon)
                {
                    return clip;
                }
            }

            return null;
        }

        private int CalculateSilenceSamples(List<AudioClipModel> clips, int maxSamples, double currentTime, double epsilon)
        {
            AudioClipModel? nextClip = FindNextClipAfter(clips, currentTime, epsilon);
            if (nextClip == null)
            {
                return maxSamples;
            }

            double silenceDuration = nextClip.StartTime - currentTime;
            if (silenceDuration <= epsilon)
            {
                return Math.Min(maxSamples, Math.Max(_channels, 1));
            }

            int silenceSamples = SecondsToSamples(silenceDuration);
            if (silenceSamples <= 0)
            {
                silenceSamples = Math.Max(_channels, 1);
            }

            return Math.Min(maxSamples, silenceSamples);
        }

        private int SecondsToSamples(double seconds)
        {
            if (seconds <= 0d)
            {
                return 0;
            }

            long frames = (long)Math.Ceiling((seconds * _sampleRate) - TimeEpsilonSeconds);
            if (frames < 1)
            {
                frames = 1;
            }

            long totalSamples = frames * _channels;
            if (totalSamples > int.MaxValue)
            {
                totalSamples = int.MaxValue - (int.MaxValue % _channels);
            }

            return (int)totalSamples;
        }

        private double SamplesToSeconds(int samples)
        {
            if (samples <= 0)
            {
                return 0d;
            }

            return (double)samples / (_sampleRate * _channels);
        }

        private FilePlaybackContext? GetOrCreateContext(string filePath)
        {
            string contextKey = NormalizeContextKey(filePath);
            if (string.IsNullOrWhiteSpace(contextKey) || !File.Exists(contextKey))
            {
                return null;
            }

            lock (_contextSync)
            {
                if (_fileContexts.TryGetValue(contextKey, out FilePlaybackContext? existing))
                {
                    return existing;
                }
            }

            AudioFormatValidationResult validation = AudioFormatValidator.ValidateReadable(contextKey);
            if (!validation.IsValid)
            {
                AppLogger.Warning($"Playback skipped invalid file: {filePath}. {validation.Message}");
                return null;
            }

            lock (_contextSync)
            {
                if (_fileContexts.TryGetValue(validation.FilePath, out FilePlaybackContext? existing))
                {
                    return existing;
                }

                try
                {
                    FilePlaybackContext context = new FilePlaybackContext(validation.FilePath, _sampleRate, _channels);
                    _fileContexts[validation.FilePath] = context;
                    return context;
                }
                catch (IOException ex)
                {
                    AppLogger.Warning($"I/O error while opening playback file: {validation.FilePath}", ex);
                    return null;
                }
                catch (InvalidOperationException ex)
                {
                    AppLogger.Warning($"Invalid playback format: {validation.FilePath}", ex);
                    return null;
                }
                catch (NotSupportedException ex)
                {
                    AppLogger.Warning($"Unsupported playback format: {validation.FilePath}", ex);
                    return null;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Unexpected playback open error: {validation.FilePath}", ex);
                    return null;
                }
            }
        }

        private void CleanupUnusedContexts(List<AudioClipModel> clips)
        {
            HashSet<string> usedFiles = new HashSet<string>(
                clips.Select(c => NormalizeContextKey(c.FilePath)).Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            lock (_contextSync)
            {
                List<string> keysToRemove = _fileContexts.Keys.Where(k => !usedFiles.Contains(k)).ToList();

                foreach (string key in keysToRemove)
                {
                    if (_fileContexts.TryGetValue(key, out FilePlaybackContext? context))
                    {
                        _fileContexts.Remove(key);
                        context.Dispose();
                    }
                }
            }
        }

        private static string NormalizeContextKey(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Could not normalize playback context path: {filePath}", ex);
                return filePath.Trim();
            }
        }

        private static ISampleProvider CreateChannelMappedProvider(ISampleProvider source, int outputChannels)
        {
            if (source.WaveFormat.Channels == outputChannels)
            {
                return source;
            }

            if (source.WaveFormat.Channels == 2 && outputChannels == 1)
            {
                StereoToMonoSampleProvider mono = new StereoToMonoSampleProvider(source)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
                return mono;
            }

            MultiplexingSampleProvider mux = new MultiplexingSampleProvider(new[] { source }, outputChannels);
            int inputChannels = source.WaveFormat.Channels;

            for (int outputChannel = 0; outputChannel < outputChannels; outputChannel++)
            {
                int inputChannel = inputChannels == 1 ? 0 : outputChannel % inputChannels;
                mux.ConnectInputToOutput(inputChannel, outputChannel);
            }

            return mux;
        }

        private void ResetContinuationLocked()
        {
            _hasContinuation = false;
            _continuationClipId = Guid.Empty;
            _continuationFilePath = null;
            _expectedFileTime = 0d;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrackSampleProvider));
            }
        }

        public void Dispose()
        {
            List<FilePlaybackContext> contexts;

            lock (_stateSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _stateVersion++;
            }

            lock (_contextSync)
            {
                contexts = _fileContexts.Values.ToList();
                _fileContexts.Clear();
            }

            foreach (FilePlaybackContext context in contexts)
            {
                context.Dispose();
            }
        }
    }

    public sealed class AudioPlaybackEngine : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly List<TrackSampleProvider> _trackProviders;
        private readonly int _sampleRate;
        private readonly int _channels;

        private WaveOutEvent? _waveOut;
        private MixingSampleProvider? _mixer;
        private double _globalTime;
        private bool _disposed;

        public AudioPlaybackEngine(int sampleRate = AppConstants.DefaultSampleRate, int channels = AppConstants.DefaultChannelCount)
        {
            _sampleRate = Math.Max(1, sampleRate);
            _channels = Math.Max(1, channels);
            _trackProviders = new List<TrackSampleProvider>();
        }

        public bool IsPlaying
        {
            get
            {
                lock (_syncRoot)
                {
                    return _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
                }
            }
        }

        public double GlobalTime
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_trackProviders.Count > 0)
                    {
                        return _trackProviders[0].CurrentGlobalTime;
                    }

                    return _globalTime;
                }
            }
        }

        public TrackSampleProvider AddTrack()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                double currentTime = GetCurrentTimeLocked();
                TrackSampleProvider provider = new TrackSampleProvider(_sampleRate, _channels);
                provider.Seek(currentTime);

                _trackProviders.Add(provider);
                RebuildMixerLocked(currentTime);

                return provider;
            }
        }

        public void RemoveTrack(TrackSampleProvider provider)
        {
            if (provider == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                double currentTime = GetCurrentTimeLocked();

                if (_trackProviders.Remove(provider))
                {
                    provider.Dispose();
                    _globalTime = currentTime;
                    RebuildMixerLocked(currentTime);
                }
            }
        }

        public List<TrackSampleProvider> GetAllProviders()
        {
            lock (_syncRoot)
            {
                return new List<TrackSampleProvider>(_trackProviders);
            }
        }

        public void Play()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                double currentTime = GetCurrentTimeLocked();

                if (_mixer == null)
                {
                    CreateMixerLocked(currentTime);
                }

                EnsureWaveOutLocked();

                if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
                {
                    _waveOut.Play();
                }
            }
        }

        public void Pause()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _waveOut?.Pause();
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _waveOut?.Stop();
                SeekAllLocked(0d);
            }
        }

        public void Seek(double time)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                SeekAllLocked(Math.Max(0d, time));
            }
        }

        private double GetCurrentTimeLocked()
        {
            if (_trackProviders.Count > 0)
            {
                return _trackProviders[0].CurrentGlobalTime;
            }

            return _globalTime;
        }

        private void SeekAllLocked(double time)
        {
            _globalTime = time;

            for (int i = 0; i < _trackProviders.Count; i++)
            {
                _trackProviders[i].Seek(time);
            }
        }

        private void RebuildMixerLocked(double currentTime)
        {
            bool resumePlayback = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;

            DisposeWaveOutLocked();
            CreateMixerLocked(currentTime);

            if (resumePlayback)
            {
                EnsureWaveOutLocked();
                _waveOut?.Play();
            }
        }

        private void CreateMixerLocked(double currentTime)
        {
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels);
            _mixer = new MixingSampleProvider(format)
            {
                ReadFully = true
            };

            for (int i = 0; i < _trackProviders.Count; i++)
            {
                _trackProviders[i].Seek(currentTime);
                _mixer.AddMixerInput(_trackProviders[i]);
            }

            _globalTime = currentTime;
        }

        private void EnsureWaveOutLocked()
        {
            if (_waveOut != null || _mixer == null)
            {
                return;
            }

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 120,
                NumberOfBuffers = 3
            };

            _waveOut.Init(_mixer);
        }

        private void DisposeWaveOutLocked()
        {
            if (_waveOut == null)
            {
                return;
            }

            try
            {
                _waveOut.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("WaveOut stop failed during dispose.", ex);
            }

            try
            {
                _waveOut.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("WaveOut dispose failed.", ex);
            }

            _waveOut = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioPlaybackEngine));
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

                DisposeWaveOutLocked();

                for (int i = 0; i < _trackProviders.Count; i++)
                {
                    _trackProviders[i].Dispose();
                }

                _trackProviders.Clear();
                _disposed = true;
            }
        }
    }
}
